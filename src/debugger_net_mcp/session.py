"""Debug session state machine managing the lifecycle of a debug session."""

import asyncio
import logging
from collections import deque
from enum import Enum
from pathlib import Path

from debugger_net_mcp.dap_client import DapClient
from debugger_net_mcp.dotnet_utils import dotnet_build

logger = logging.getLogger(__name__)

MAX_OUTPUT_LINES = 200


class SessionState(Enum):
    IDLE = "idle"
    INITIALIZING = "initializing"
    RUNNING = "running"
    STOPPED = "stopped"
    TERMINATED = "terminated"


class DebugSession:
    """Manages a single debug session against netcoredbg."""

    def __init__(self):
        self._dap = DapClient()
        self._state = SessionState.IDLE
        self._pending_breakpoints: dict[str, list[dict]] = {}  # file -> [{line, condition?}]
        self._output_lines: deque[str] = deque(maxlen=MAX_OUTPUT_LINES)
        self._thread_id: int | None = None
        self._stop_reason: str | None = None
        self._stop_location: dict | None = None
        self._output_listener: asyncio.Task | None = None
        self._terminated_event = asyncio.Event()
        self._waiting_for_stop = False

    @property
    def state(self) -> SessionState:
        return self._state

    @property
    def is_active(self) -> bool:
        return self._state not in (SessionState.IDLE, SessionState.TERMINATED)

    def _require_state(self, *allowed: SessionState) -> None:
        if self._state not in allowed:
            raise RuntimeError(
                f"Operation not allowed in state '{self._state.value}'. "
                f"Expected: {', '.join(s.value for s in allowed)}"
            )

    # --- Initialize + Launch/Attach ---

    async def launch(
        self, project_path: str, stop_at_entry: bool = True, args: list[str] | None = None
    ) -> dict:
        """Build the project, start netcoredbg, and launch the debuggee."""
        self._require_state(SessionState.IDLE, SessionState.TERMINATED)
        self._reset()
        self._state = SessionState.INITIALIZING

        # Build
        build_output, dll_path = await dotnet_build(project_path)
        if dll_path is None:
            self._state = SessionState.IDLE
            return {"success": False, "error": "Build failed", "output": build_output}

        # Start DAP adapter
        await self._dap.start()

        # Initialize
        await self._dap.send_request("initialize", {
            "clientID": "debugger-net-mcp",
            "adapterID": "coreclr",
            "pathFormat": "path",
            "linesStartAt1": True,
            "columnsStartAt1": True,
            "supportsRunInTerminalRequest": False,
        })

        # Launch - must come BEFORE configurationDone per DAP spec
        launch_args = {
            "program": dll_path,
            "cwd": str(Path(dll_path).parent),
            "stopAtEntry": stop_at_entry,
        }
        if args:
            launch_args["args"] = args

        await self._dap.send_request("launch", launch_args)

        # Wait for the "initialized" event BEFORE sending breakpoints
        initialized = await self._dap.wait_for_event("initialized", timeout=15.0)
        if initialized is None:
            logger.warning("Timed out waiting for 'initialized' event")

        # Now set pending breakpoints
        await self._send_pending_breakpoints()

        # Configuration done - signals we're done configuring
        await self._dap.send_request("configurationDone")

        # Start output listener
        self._output_listener = asyncio.create_task(self._listen_outputs())

        # Wait for the first stopped event (entry point) or keep running
        if stop_at_entry:
            stopped = await self._wait_for_stop(timeout=10.0)
            if stopped:
                return {
                    "success": True,
                    "state": self._state.value,
                    "reason": self._stop_reason,
                    "location": self._stop_location,
                    "build_output": _last_lines(build_output, 5),
                }
        else:
            self._state = SessionState.RUNNING

        return {
            "success": True,
            "state": self._state.value,
            "build_output": _last_lines(build_output, 5),
        }

    async def attach(self, process_id: int) -> dict:
        """Attach to a running .NET process."""
        self._require_state(SessionState.IDLE, SessionState.TERMINATED)
        self._reset()
        self._state = SessionState.INITIALIZING

        await self._dap.start()

        await self._dap.send_request("initialize", {
            "clientID": "debugger-net-mcp",
            "adapterID": "coreclr",
            "pathFormat": "path",
            "linesStartAt1": True,
            "columnsStartAt1": True,
        })

        # Attach - must come BEFORE configurationDone per DAP spec
        await self._dap.send_request("attach", {"processId": process_id})

        # Wait for the "initialized" event BEFORE sending breakpoints
        initialized = await self._dap.wait_for_event("initialized", timeout=15.0)
        if initialized is None:
            logger.warning("Timed out waiting for 'initialized' event")

        await self._send_pending_breakpoints()
        await self._dap.send_request("configurationDone")

        self._output_listener = asyncio.create_task(self._listen_outputs())
        self._state = SessionState.RUNNING

        return {"success": True, "state": self._state.value}

    # --- Breakpoints ---

    async def set_breakpoint(
        self, file_path: str, line: int, condition: str | None = None
    ) -> dict:
        """Set a breakpoint. Works before or during a session."""
        bp = {"line": line}
        if condition:
            bp["condition"] = condition

        if file_path not in self._pending_breakpoints:
            self._pending_breakpoints[file_path] = []

        # Replace existing breakpoint on same line
        self._pending_breakpoints[file_path] = [
            b for b in self._pending_breakpoints[file_path] if b["line"] != line
        ]
        self._pending_breakpoints[file_path].append(bp)

        # If session active, send immediately
        if self.is_active:
            return await self._set_breakpoints_for_file(file_path)

        return {"status": "pending", "file": file_path, "line": line}

    async def remove_breakpoints(self, file_path: str) -> dict:
        """Remove all breakpoints from a file."""
        self._pending_breakpoints.pop(file_path, None)

        if self.is_active:
            await self._dap.send_request("setBreakpoints", {
                "source": {"path": file_path},
                "breakpoints": [],
            })

        return {"status": "cleared", "file": file_path}

    # --- Execution Control ---

    async def continue_execution(self, timeout: float = 30.0) -> dict:
        """Continue execution and wait for the next stop event."""
        self._require_state(SessionState.STOPPED, SessionState.RUNNING)

        if self._state == SessionState.STOPPED:
            self._state = SessionState.RUNNING
            await self._dap.send_request("continue", {"threadId": self._thread_id or 1})

        stopped = await self._wait_for_stop(timeout=timeout)
        if stopped:
            return {
                "stopped": True,
                "reason": self._stop_reason,
                "location": self._stop_location,
            }
        return {"stopped": False, "note": "Program still running after timeout"}

    async def step_over(self, timeout: float = 10.0) -> dict:
        """Step over (next line)."""
        return await self._step("next", timeout)

    async def step_into(self, timeout: float = 10.0) -> dict:
        """Step into (enter function)."""
        return await self._step("stepIn", timeout)

    async def step_out(self, timeout: float = 10.0) -> dict:
        """Step out (exit function)."""
        return await self._step("stepOut", timeout)

    async def pause(self) -> dict:
        """Pause execution."""
        self._require_state(SessionState.RUNNING)

        # Check if already stopped (event pending but state not updated yet)
        pending = self._dap.drain_events("stopped")
        if pending:
            await self._handle_stopped_event(pending[-1])
            return {"stopped": True, "reason": self._stop_reason, "location": self._stop_location}

        await self._dap.send_request("pause", {"threadId": self._thread_id or 1})

        stopped = await self._wait_for_stop(timeout=5.0)
        if stopped:
            return {"stopped": True, "reason": self._stop_reason, "location": self._stop_location}
        return {"stopped": False, "note": "Pause requested but no stop event received"}

    # --- Inspection ---

    async def stacktrace(self, levels: int = 20) -> dict:
        """Get the current call stack."""
        self._require_state(SessionState.STOPPED)

        resp = await self._dap.send_request("stackTrace", {
            "threadId": self._thread_id or 1,
            "startFrame": 0,
            "levels": levels,
        })

        frames = []
        for f in resp.get("body", {}).get("stackFrames", []):
            source = f.get("source", {})
            frames.append({
                "id": f["id"],
                "name": f.get("name", "?"),
                "file": source.get("path", source.get("name", "?")),
                "line": f.get("line", 0),
                "column": f.get("column", 0),
            })

        return {"frames": frames}

    async def variables(
        self, frame_index: int = 0, expand: str | None = None, scope: str = "Locals"
    ) -> dict:
        """Get variables in the current scope. Use expand to drill into an object."""
        self._require_state(SessionState.STOPPED)

        # Get stack frames to find the frame ID
        st = await self._dap.send_request("stackTrace", {
            "threadId": self._thread_id or 1,
            "startFrame": 0,
            "levels": frame_index + 1,
        })
        frames = st.get("body", {}).get("stackFrames", [])
        if frame_index >= len(frames):
            return {"error": f"Frame index {frame_index} out of range (have {len(frames)})"}

        frame_id = frames[frame_index]["id"]

        # Get scopes
        scopes_resp = await self._dap.send_request("scopes", {"frameId": frame_id})
        target_scope = None
        for s in scopes_resp.get("body", {}).get("scopes", []):
            if scope.lower() in s.get("name", "").lower():
                target_scope = s
                break

        if not target_scope:
            available = [s.get("name") for s in scopes_resp.get("body", {}).get("scopes", [])]
            return {"error": f"Scope '{scope}' not found. Available: {available}"}

        # Get variables
        var_ref = target_scope["variablesReference"]
        vars_resp = await self._dap.send_request("variables", {
            "variablesReference": var_ref,
        })

        variables = []
        for v in vars_resp.get("body", {}).get("variables", []):
            var_info = {
                "name": v["name"],
                "value": v.get("value", ""),
                "type": v.get("type", ""),
            }
            if v.get("variablesReference", 0) > 0:
                var_info["expandable"] = True
                var_info["variablesReference"] = v["variablesReference"]
            variables.append(var_info)

        # If expand requested, drill into a specific variable
        if expand:
            for v in variables:
                if v["name"] == expand and v.get("expandable"):
                    child_resp = await self._dap.send_request("variables", {
                        "variablesReference": v["variablesReference"],
                    })
                    v["children"] = [
                        {
                            "name": c["name"],
                            "value": c.get("value", ""),
                            "type": c.get("type", ""),
                            "expandable": c.get("variablesReference", 0) > 0,
                        }
                        for c in child_resp.get("body", {}).get("variables", [])
                    ]
                    break

        return {"variables": variables}

    async def evaluate(self, expression: str, frame_index: int = 0) -> dict:
        """Evaluate a C# expression in the current debug context."""
        self._require_state(SessionState.STOPPED)

        st = await self._dap.send_request("stackTrace", {
            "threadId": self._thread_id or 1,
            "startFrame": 0,
            "levels": frame_index + 1,
        })
        frames = st.get("body", {}).get("stackFrames", [])
        if frame_index >= len(frames):
            return {"error": f"Frame index {frame_index} out of range"}

        frame_id = frames[frame_index]["id"]

        resp = await self._dap.send_request("evaluate", {
            "expression": expression,
            "frameId": frame_id,
            "context": "watch",
        })

        body = resp.get("body", {})
        result = {
            "result": body.get("result", ""),
            "type": body.get("type", ""),
        }
        if body.get("variablesReference", 0) > 0:
            result["expandable"] = True
        return result

    # --- Session Management ---

    async def disconnect(self) -> dict:
        """Disconnect and terminate the debug session."""
        if self._state == SessionState.IDLE:
            return {"status": "already idle"}

        try:
            if self._dap.is_running:
                await self._dap.send_request("disconnect", {"terminateDebuggee": True})
        except Exception:
            pass

        await self._cleanup()
        return {"status": "disconnected"}

    def status(self) -> dict:
        """Get current session status."""
        result: dict = {
            "state": self._state.value,
        }

        if self._stop_location:
            result["location"] = self._stop_location
        if self._stop_reason:
            result["reason"] = self._stop_reason

        output = list(self._output_lines)
        if output:
            result["recent_output"] = output[-30:]

        bp_summary = {}
        for f, bps in self._pending_breakpoints.items():
            bp_summary[f] = [b["line"] for b in bps]
        if bp_summary:
            result["breakpoints"] = bp_summary

        return result

    # --- Internal ---

    def _reset(self) -> None:
        self._output_lines.clear()
        self._thread_id = None
        self._stop_reason = None
        self._stop_location = None
        self._terminated_event.clear()
        self._waiting_for_stop = False

    async def _cleanup(self) -> None:
        if self._output_listener:
            self._output_listener.cancel()
            try:
                await self._output_listener
            except asyncio.CancelledError:
                pass
            self._output_listener = None

        await self._dap.stop()
        self._state = SessionState.TERMINATED

    async def _step(self, command: str, timeout: float) -> dict:
        """Generic step command."""
        self._require_state(SessionState.STOPPED)
        self._state = SessionState.RUNNING

        await self._dap.send_request(command, {"threadId": self._thread_id or 1})

        stopped = await self._wait_for_stop(timeout=timeout)
        if stopped:
            return {
                "stopped": True,
                "reason": self._stop_reason,
                "location": self._stop_location,
            }
        return {"stopped": False, "note": f"{command} did not complete within timeout"}

    async def _handle_stopped_event(self, event: dict) -> None:
        """Process a stopped event: update state, thread, reason, and location."""
        body = event.get("body", {})
        self._thread_id = body.get("threadId", self._thread_id)
        self._stop_reason = body.get("reason", "unknown")
        self._state = SessionState.STOPPED
        try:
            st = await self._dap.send_request("stackTrace", {
                "threadId": self._thread_id or 1,
                "startFrame": 0,
                "levels": 1,
            })
            frames = st.get("body", {}).get("stackFrames", [])
            if frames:
                f = frames[0]
                source = f.get("source", {})
                self._stop_location = {
                    "file": source.get("path", source.get("name", "?")),
                    "line": f.get("line", 0),
                    "name": f.get("name", "?"),
                }
            else:
                self._stop_location = None
        except Exception:
            self._stop_location = None

    async def _wait_for_stop(self, timeout: float) -> bool:
        """Wait for a 'stopped' event from DAP. Returns True if stopped."""
        self._waiting_for_stop = True
        try:
            event = await self._dap.wait_for_event("stopped", timeout=timeout)
            if event is None:
                term = self._dap.drain_events("terminated")
                if term:
                    await self._cleanup()
                return False
            await self._handle_stopped_event(event)
            return True
        finally:
            self._waiting_for_stop = False

    async def _send_pending_breakpoints(self) -> None:
        """Send all registered breakpoints to DAP."""
        for file_path in list(self._pending_breakpoints):
            try:
                await self._set_breakpoints_for_file(file_path)
            except Exception as e:
                logger.warning("Failed to set breakpoints for %s: %s", file_path, e)

    async def _set_breakpoints_for_file(self, file_path: str) -> dict:
        """Send breakpoints for a single file to DAP."""
        bps = self._pending_breakpoints.get(file_path, [])
        dap_bps = []
        for bp in bps:
            dap_bp: dict = {"line": bp["line"]}
            if "condition" in bp:
                dap_bp["condition"] = bp["condition"]
            dap_bps.append(dap_bp)

        resp = await self._dap.send_request("setBreakpoints", {
            "source": {"path": file_path},
            "breakpoints": dap_bps,
        })

        result_bps = []
        for b in resp.get("body", {}).get("breakpoints", []):
            result_bps.append({
                "verified": b.get("verified", False),
                "line": b.get("line", 0),
                "message": b.get("message"),
            })

        return {"file": file_path, "breakpoints": result_bps}

    async def _listen_outputs(self) -> None:
        """Background task to capture program output and stopped events."""
        try:
            while self.is_active:
                event = await self._dap.wait_for_event("output", timeout=1.0)
                if event:
                    body = event.get("body", {})
                    text = body.get("output", "").rstrip("\n")
                    if text:
                        self._output_lines.append(text)

                # Handle stopped events when nobody else is waiting for them
                if not self._waiting_for_stop:
                    stopped_events = self._dap.drain_events("stopped")
                    if stopped_events and self._state == SessionState.RUNNING:
                        await self._handle_stopped_event(stopped_events[-1])

                # Check for terminated
                term_events = self._dap.drain_events("terminated")
                if term_events:
                    self._terminated_event.set()
                    if self._state in (SessionState.RUNNING, SessionState.STOPPED):
                        self._state = SessionState.TERMINATED
                    break

                exit_events = self._dap.drain_events("exited")
                if exit_events:
                    body = exit_events[-1].get("body", {})
                    code = body.get("exitCode", -1)
                    self._output_lines.append(f"[Process exited with code {code}]")
        except asyncio.CancelledError:
            raise
        except Exception:
            if self.is_active:
                logger.exception("Output listener error")


# Singleton session
_session: DebugSession | None = None


def get_session() -> DebugSession:
    """Get or create the singleton debug session."""
    global _session
    if _session is None:
        _session = DebugSession()
    return _session


def _last_lines(text: str, n: int) -> str:
    lines = text.strip().splitlines()
    return "\n".join(lines[-n:]) if len(lines) > n else text.strip()
