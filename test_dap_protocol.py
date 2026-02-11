#!/usr/bin/env python3
"""Raw DAP protocol test for netcoredbg.

Tests two different DAP orderings to determine which one works:
  Order A (standard DAP spec):
    initialize → launch(stopAtEntry=true) → wait "initialized" → setBreakpoints → configurationDone → wait "stopped"
  Order B (netcoredbg-specific):
    initialize → wait "initialized" → setBreakpoints → configurationDone → launch(stopAtEntry=true) → wait "stopped"
"""

import asyncio
import json
import os
import sys
from pathlib import Path

NETCOREDBG = os.environ.get("NETCOREDBG_PATH", os.path.expanduser("~/.local/bin/netcoredbg"))
DOTNET_ROOT = os.environ.get("DOTNET_ROOT", os.path.expanduser("~/.dotnet"))
PROJECT_DIR = "/tmp/debug-test/HelloDebug"
DLL_PATH = f"{PROJECT_DIR}/bin/Debug/net8.0/HelloDebug.dll"
PROGRAM_CS = f"{PROJECT_DIR}/Program.cs"
BP_LINE = 6  # var sum = 0;


class DapRawClient:
    def __init__(self):
        self.proc = None
        self.seq = 1
        self.pending: dict[int, asyncio.Future] = {}
        self.events: dict[str, asyncio.Queue] = {}
        self._reader_task = None
        self._buffer = b""

    async def start(self):
        netcoredbg_dir = str(Path(NETCOREDBG).parent)
        # Look for libdbgshim.so alongside netcoredbg or in lib/netcoredbg
        lib_dirs = [netcoredbg_dir]
        lib_netcoredbg = os.path.join(os.path.dirname(netcoredbg_dir), "lib", "netcoredbg")
        if os.path.isdir(lib_netcoredbg):
            lib_dirs.append(lib_netcoredbg)

        env = os.environ.copy()
        env["DOTNET_ROOT"] = DOTNET_ROOT
        env["PATH"] = f"{DOTNET_ROOT}:{env.get('PATH', '')}"
        env["LD_LIBRARY_PATH"] = ":".join(lib_dirs) + ":" + env.get("LD_LIBRARY_PATH", "")
        # Remove proxy vars that cause issues
        for k in ["http_proxy", "https_proxy", "HTTP_PROXY", "HTTPS_PROXY"]:
            env.pop(k, None)

        self.proc = await asyncio.create_subprocess_exec(
            NETCOREDBG,
            "--interpreter=vscode",
            "--engineLogging=/tmp/engine.log",
            stdin=asyncio.subprocess.PIPE,
            stdout=asyncio.subprocess.PIPE,
            stderr=asyncio.subprocess.PIPE,
            env=env,
        )
        self._reader_task = asyncio.create_task(self._reader_loop())

    async def _reader_loop(self):
        assert self.proc and self.proc.stdout
        while True:
            # Read Content-Length header
            header_line = await self.proc.stdout.readline()
            if not header_line:
                break
            header = header_line.decode("utf-8").strip()
            if not header.startswith("Content-Length:"):
                continue
            content_length = int(header.split(":")[1].strip())

            # Read blank line separator
            await self.proc.stdout.readline()

            # Read body
            body = await self.proc.stdout.readexactly(content_length)
            msg = json.loads(body.decode("utf-8"))

            if msg.get("type") == "response":
                seq = msg.get("request_seq")
                if seq in self.pending:
                    self.pending[seq].set_result(msg)
            elif msg.get("type") == "event":
                event_name = msg.get("event", "")
                if event_name not in self.events:
                    self.events[event_name] = asyncio.Queue()
                await self.events[event_name].put(msg)

    async def send_request(self, command: str, arguments: dict | None = None) -> dict:
        msg = {
            "seq": self.seq,
            "type": "request",
            "command": command,
        }
        if arguments:
            msg["arguments"] = arguments
        seq = self.seq
        self.seq += 1

        loop = asyncio.get_event_loop()
        future = loop.create_future()
        self.pending[seq] = future

        data = json.dumps(msg)
        frame = f"Content-Length: {len(data)}\r\n\r\n{data}"
        self.proc.stdin.write(frame.encode("utf-8"))
        await self.proc.stdin.drain()

        return await asyncio.wait_for(future, timeout=30)

    async def wait_event(self, event_name: str, timeout: float = 30) -> dict:
        if event_name not in self.events:
            self.events[event_name] = asyncio.Queue()
        return await asyncio.wait_for(self.events[event_name].get(), timeout=timeout)

    async def stop(self):
        if self.proc:
            try:
                if self.proc.returncode is None:
                    self.proc.terminate()
                    await asyncio.wait_for(self.proc.wait(), timeout=5)
            except (ProcessLookupError, asyncio.TimeoutError):
                try:
                    if self.proc.returncode is None:
                        self.proc.kill()
                except ProcessLookupError:
                    pass
        if self._reader_task:
            self._reader_task.cancel()
            try:
                await self._reader_task
            except (asyncio.CancelledError, Exception):
                pass


async def test_order_a():
    """Order A: initialize → launch → wait initialized → setBreakpoints → configurationDone → wait stopped"""
    print("\n" + "=" * 60)
    print("Testing ORDER A (standard DAP spec)")
    print("initialize → launch(stopAtEntry) → initialized → setBreakpoints → configurationDone → stopped")
    print("=" * 60)

    client = DapRawClient()
    try:
        await client.start()

        # 1. initialize
        resp = await client.send_request("initialize", {
            "clientID": "test",
            "clientName": "test",
            "adapterID": "coreclr",
            "pathFormat": "path",
            "linesStartAt1": True,
            "columnsStartAt1": True,
            "supportsRunInTerminalRequest": False,
        })
        assert resp["success"], f"initialize failed: {resp}"
        print("  [PASS] initialize → capabilities received")

        # 2. launch (with stopAtEntry)
        resp = await client.send_request("launch", {
            "name": ".NET Core Launch",
            "type": "coreclr",
            "request": "launch",
            "program": DLL_PATH,
            "cwd": PROJECT_DIR,
            "stopAtEntry": True,
            "console": "internalConsole",
        })
        assert resp["success"], f"launch failed: {resp}"
        print("  [PASS] launch → accepted")

        # 3. wait for initialized event
        evt = await client.wait_event("initialized", timeout=15)
        print("  [PASS] initialized event received")

        # 4. setBreakpoints
        resp = await client.send_request("setBreakpoints", {
            "source": {"path": PROGRAM_CS},
            "breakpoints": [{"line": BP_LINE}],
        })
        assert resp["success"], f"setBreakpoints failed: {resp}"
        bps = resp.get("body", {}).get("breakpoints", [])
        print(f"  [PASS] setBreakpoints → {len(bps)} breakpoint(s), verified={bps[0].get('verified') if bps else '?'}")

        # 5. configurationDone
        resp = await client.send_request("configurationDone")
        assert resp["success"], f"configurationDone failed: {resp}"
        print("  [PASS] configurationDone")

        # 6. wait for stopped event
        evt = await client.wait_event("stopped", timeout=15)
        reason = evt.get("body", {}).get("reason", "?")
        thread_id = evt.get("body", {}).get("threadId")
        print(f"  [PASS] stopped event: reason={reason}, threadId={thread_id}")

        # Run the full validation
        await _validate_debug_session(client, thread_id or 1)

        print("\n  ORDER A: ALL TESTS PASSED ✓")
        return True

    except Exception as e:
        print(f"\n  ORDER A FAILED: {e}")
        return False
    finally:
        try:
            await client.send_request("disconnect", {"terminateDebuggee": True})
        except Exception:
            pass
        await client.stop()


async def test_order_b():
    """Order B: initialize → wait initialized → setBreakpoints → configurationDone → launch → wait stopped"""
    print("\n" + "=" * 60)
    print("Testing ORDER B (netcoredbg-specific)")
    print("initialize → initialized → setBreakpoints → configurationDone → launch(stopAtEntry) → stopped")
    print("=" * 60)

    client = DapRawClient()
    try:
        await client.start()

        # 1. initialize
        resp = await client.send_request("initialize", {
            "clientID": "test",
            "clientName": "test",
            "adapterID": "coreclr",
            "pathFormat": "path",
            "linesStartAt1": True,
            "columnsStartAt1": True,
            "supportsRunInTerminalRequest": False,
        })
        assert resp["success"], f"initialize failed: {resp}"
        print("  [PASS] initialize → capabilities received")

        # 2. wait for initialized event
        evt = await client.wait_event("initialized", timeout=15)
        print("  [PASS] initialized event received")

        # 3. setBreakpoints
        resp = await client.send_request("setBreakpoints", {
            "source": {"path": PROGRAM_CS},
            "breakpoints": [{"line": BP_LINE}],
        })
        assert resp["success"], f"setBreakpoints failed: {resp}"
        bps = resp.get("body", {}).get("breakpoints", [])
        print(f"  [PASS] setBreakpoints → {len(bps)} breakpoint(s), verified={bps[0].get('verified') if bps else '?'}")

        # 4. configurationDone
        resp = await client.send_request("configurationDone")
        assert resp["success"], f"configurationDone failed: {resp}"
        print("  [PASS] configurationDone")

        # 5. launch (with stopAtEntry)
        resp = await client.send_request("launch", {
            "name": ".NET Core Launch",
            "type": "coreclr",
            "request": "launch",
            "program": DLL_PATH,
            "cwd": PROJECT_DIR,
            "stopAtEntry": True,
            "console": "internalConsole",
        })
        assert resp["success"], f"launch failed: {resp}"
        print("  [PASS] launch → accepted")

        # 6. wait for stopped event
        evt = await client.wait_event("stopped", timeout=15)
        reason = evt.get("body", {}).get("reason", "?")
        thread_id = evt.get("body", {}).get("threadId")
        print(f"  [PASS] stopped event: reason={reason}, threadId={thread_id}")

        # Run the full validation
        await _validate_debug_session(client, thread_id or 1)

        print("\n  ORDER B: ALL TESTS PASSED ✓")
        return True

    except Exception as e:
        print(f"\n  ORDER B FAILED: {e}")
        return False
    finally:
        try:
            await client.send_request("disconnect", {"terminateDebuggee": True})
        except Exception:
            pass
        await client.stop()


async def _validate_debug_session(client: DapRawClient, thread_id: int):
    """Validate the full debug session once stopped."""

    # threads
    resp = await client.send_request("threads")
    assert resp["success"], f"threads failed: {resp}"
    threads = resp.get("body", {}).get("threads", [])
    print(f"  [PASS] threads → {len(threads)} thread(s)")

    # stackTrace
    resp = await client.send_request("stackTrace", {
        "threadId": thread_id,
        "startFrame": 0,
        "levels": 20,
    })
    assert resp["success"], f"stackTrace failed: {resp}"
    frames = resp.get("body", {}).get("stackFrames", [])
    frame = frames[0] if frames else {}
    source_path = frame.get("source", {}).get("path", "?")
    line = frame.get("line", "?")
    print(f"  [PASS] stackTrace → {len(frames)} frame(s), top: {source_path}:{line}")

    # scopes
    frame_id = frame.get("id", 0)
    resp = await client.send_request("scopes", {"frameId": frame_id})
    assert resp["success"], f"scopes failed: {resp}"
    scopes = resp.get("body", {}).get("scopes", [])
    print(f"  [PASS] scopes → {len(scopes)} scope(s)")

    # variables for each scope
    for scope in scopes:
        var_ref = scope.get("variablesReference", 0)
        if var_ref == 0:
            continue
        resp = await client.send_request("variables", {"variablesReference": var_ref})
        assert resp["success"], f"variables failed: {resp}"
        variables = resp.get("body", {}).get("variables", [])
        var_names = [v.get("name") for v in variables]
        print(f"  [PASS] variables ({scope.get('name', '?')}) → {var_names}")

    # evaluate
    resp = await client.send_request("evaluate", {
        "expression": "name.Length",
        "frameId": frame_id,
        "context": "watch",
    })
    if resp["success"]:
        result = resp.get("body", {}).get("result", "?")
        print(f"  [PASS] evaluate('name.Length') → {result}")
    else:
        # May fail if stopped at entry before name is assigned
        print(f"  [SKIP] evaluate('name.Length') → not available at current stop point")

    # continue
    resp = await client.send_request("continue", {"threadId": thread_id})
    assert resp["success"], f"continue failed: {resp}"
    print("  [PASS] continue → execution resumed")

    # Wait for either another stopped event (breakpoint) or terminated
    try:
        evt = await client.wait_event("stopped", timeout=10)
        reason = evt.get("body", {}).get("reason", "?")
        print(f"  [PASS] stopped again: reason={reason}")

        # If we hit the breakpoint, continue to let the program finish
        resp = await client.send_request("continue", {"threadId": thread_id})
        print("  [PASS] continue (final) → let program finish")
    except asyncio.TimeoutError:
        print("  [INFO] No second stop (program may have exited)")

    # Wait for terminated/exited events
    try:
        evt = await client.wait_event("terminated", timeout=10)
        print("  [PASS] terminated event received")
    except asyncio.TimeoutError:
        print("  [INFO] No terminated event (may have already been received)")

    # disconnect
    resp = await client.send_request("disconnect", {"terminateDebuggee": True})
    assert resp["success"], f"disconnect failed: {resp}"
    print("  [PASS] disconnect → clean shutdown")


async def main():
    print(f"netcoredbg: {NETCOREDBG}")
    print(f"DOTNET_ROOT: {DOTNET_ROOT}")
    print(f"DLL: {DLL_PATH}")
    print(f"Breakpoint line: {BP_LINE}")

    result_a = await test_order_a()
    result_b = await test_order_b()

    print("\n" + "=" * 60)
    print("RESULTS:")
    print(f"  Order A (standard DAP): {'PASS' if result_a else 'FAIL'}")
    print(f"  Order B (netcoredbg-specific): {'PASS' if result_b else 'FAIL'}")
    print("=" * 60)

    if result_a:
        print("\nRECOMMENDATION: Use Order A (standard DAP spec)")
    elif result_b:
        print("\nRECOMMENDATION: Use Order B (netcoredbg-specific)")
    else:
        print("\nBOTH ORDERS FAILED - check /tmp/engine.log for details")

    return 0 if (result_a or result_b) else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
