# Architecture

**Analysis Date:** 2026-02-22

## Pattern Overview

**Overall:** Event-driven async MCP server with layered protocol bridge architecture.

The system implements a **state machine-driven async client** that bridges between:
1. **MCP (Model Context Protocol)** - Stdio-based request/response protocol with Claude
2. **DAP (Debug Adapter Protocol)** - Async JSON message protocol with netcoredbg
3. **.NET Debugger (netcoredbg)** - Subprocess communicating via DAP over stdin/stdout

**Key Characteristics:**
- **Async-first**: Built entirely on `asyncio` with non-blocking subprocess communication
- **Message-driven**: DAP works via Content-Length framed JSON messages dispatched to appropriate handlers
- **State-locked**: Session enforces valid state transitions (IDLE → INITIALIZING → RUNNING/STOPPED → TERMINATED)
- **Event-queueing**: Incoming DAP events buffered in per-event-type queues for consumption by waiters
- **Singleton session**: Single global debug session per MCP server instance; tools reuse state

## Layers

**MCP Server Layer (Presentation):**
- Purpose: Expose 14 debug tools as FastMCP handlers that users call via Claude Code
- Location: `src/debugger_net_mcp/server.py`
- Contains: Tool entry points (`debug_launch`, `debug_set_breakpoint`, `debug_continue`, etc.)
- Depends on: `DebugSession` singleton from `session.py`
- Used by: Claude Code via stdio

**Session Management Layer (State Machine):**
- Purpose: Manage the lifecycle and state of a single debug session; enforce valid state transitions
- Location: `src/debugger_net_mcp/session.py`
- Contains: `DebugSession` class with methods for launch/attach, breakpoints, execution control, inspection
- Depends on: `DapClient` for protocol communication, `dotnet_build` for project building
- Used by: MCP Server tools

**DAP Protocol Layer (Transport):**
- Purpose: Handle all communication with netcoredbg via the Debug Adapter Protocol
- Location: `src/debugger_net_mcp/dap_client.py`
- Contains: `DapClient` class managing subprocess, message framing, dispatch, and event queueing
- Depends on: `asyncio` subprocess, JSON serialization, netcoredbg binary location resolution
- Used by: DebugSession for all protocol operations

**Utilities Layer:**
- Purpose: Support project building and environment setup
- Location: `src/debugger_net_mcp/dotnet_utils.py`
- Contains: `dotnet_build()` function to compile .NET projects and extract DLL paths
- Depends on: `dotnet` CLI, subprocess communication
- Used by: DebugSession.launch()

## Data Flow

**Launch Flow:**
```
1. MCP Tool: debug_launch(project_path, stop_at_entry)
   └─> DebugSession.launch()
       ├─> dotnet_build(project_path) [subprocess: dotnet build]
       ├─> DapClient.start() [subprocess: netcoredbg --interpreter=vscode]
       ├─> DapClient.send_request("initialize", {...})
       ├─> DapClient.send_request("launch", {"program": dll_path, "stopAtEntry": True})
       ├─> DapClient.wait_for_event("initialized", timeout=15)
       ├─> DebugSession._send_pending_breakpoints()
       ├─> DapClient.send_request("configurationDone")
       ├─> asyncio.create_task(DebugSession._listen_outputs()) [background output listener]
       └─> DapClient.wait_for_event("stopped", timeout=10) [if stopAtEntry=True]
           └─> DebugSession._handle_stopped_event() [updates state, thread_id, stop_reason, stop_location]
```

**Execution Control Flow (step, continue):**
```
1. MCP Tool: debug_continue() / debug_step_over() / debug_step_into() / etc.
   └─> DebugSession.continue_execution() / step_*()
       ├─> DebugSession._require_state() [verify STOPPED state]
       ├─> DapClient.send_request("continue" | "next" | "stepIn" | etc., {"threadId": ...})
       ├─> DebugSession._wait_for_stop(timeout) [waits for DAP "stopped" event]
       │   └─> DapClient.wait_for_event("stopped", timeout)
       │       └─> Dequeues from _event_queues["stopped"]
       └─> DebugSession._handle_stopped_event() [updates state and location]
           └─> DapClient.send_request("stackTrace") [fetch current frame location]
```

**State Management:**
- **IDLE**: No session active. Tools return "not running" or initialize a new session.
- **INITIALIZING**: Session being set up. Cannot run tools yet.
- **RUNNING**: Program executing. Can pause(), set breakpoints (deferred).
- **STOPPED**: Program paused at breakpoint/exception. Can inspect, step, evaluate.
- **TERMINATED**: Session ended. Can relaunch or disconnect.

State transitions are enforced by `_require_state()` in DebugSession.

## Key Abstractions

**DapClient:**
- Purpose: Encapsulate all DAP protocol details (message framing, dispatch, event queueing)
- Examples: `src/debugger_net_mcp/dap_client.py` lines 71–276
- Pattern:
  - Manages async subprocess via `asyncio.create_subprocess_exec()`
  - Content-Length framing for JSON messages (`_write_message`, `_read_message`)
  - Request/response correlation by sequence numbers (`_seq`, `_pending`)
  - Event queuing: incoming events enqueued to `_event_queues[event_name]`
  - Background reader task (`_reader_loop`) continuously parses and dispatches messages

**DebugSession:**
- Purpose: State machine managing a single debug lifecycle
- Examples: `src/debugger_net_mcp/session.py` lines 25–571
- Pattern:
  - Singleton via `get_session()` global function
  - State enum: `SessionState` (IDLE, INITIALIZING, RUNNING, STOPPED, TERMINATED)
  - Pending breakpoints stored as dict[file -> list[{line, condition?}]]
  - Output captured in bounded deque (MAX_OUTPUT_LINES=200)
  - Background output listener task (`_listen_outputs()`) consumes "output" and "stopped" events

**FastMCP Server:**
- Purpose: Expose async functions as MCP tools
- Examples: `src/debugger_net_mcp/server.py` lines 1–206
- Pattern:
  - Decorated functions with `@mcp.tool()`
  - Each tool is async, retrieves singleton session, calls session method, returns dict result
  - MCP runs stdin/stdout transport via `mcp.run(transport="stdio")`

## Entry Points

**Python Module Entry:**
- Location: `src/debugger_net_mcp/__main__.py`
- Triggers: `python -m debugger_net_mcp`
- Responsibilities: Import and call `main()` from `server.py`

**MCP Server Main:**
- Location: `src/debugger_net_mcp/server.py`, function `main()` (line 205)
- Triggers: MCP CLI or direct Python invocation
- Responsibilities: Initialize FastMCP, register tools, start stdio transport

**Tool Handlers:**
- Location: `src/debugger_net_mcp/server.py`, lines 21–203 (@mcp.tool() decorated functions)
- Triggers: Claude Code calls via MCP
- Responsibilities:
  - `debug_launch`: Build project, start session
  - `debug_attach`: Attach to running process
  - `debug_set_breakpoint`: Register breakpoint (deferred or live)
  - `debug_continue`: Resume execution
  - `debug_step_*`: Step through code
  - `debug_stacktrace`, `debug_variables`, `debug_evaluate`: Inspect state
  - `debug_pause`: Interrupt execution
  - `debug_disconnect`: Clean up session
  - `debug_status`: Report current state

## Error Handling

**Strategy:** Explicit result dicts with `"success"` flag; exceptions caught at tool boundary.

**Patterns:**
- Tools return `{"success": False, "error": "reason"}` on failure
- State violations raise `RuntimeError` with allowed states listed
- DAP protocol errors caught in `_dispatch()`: unpacks `message` and `body.error.format`
- Subprocess errors: timeouts return None, caught in callers with None-checks
- Build failures: `dotnet_build()` returns (output, None); caller checks for None
- Connection errors: `DapClient.stop()` rejects all pending futures with `ConnectionError`

## Cross-Cutting Concerns

**Logging:**
- Standard Python `logging` module, configured to INFO level in `server.py`
- Loggers per module: `logger = logging.getLogger(__name__)`
- Debug info: message framing details, subprocess lifecycle, timeouts, exceptions

**Validation:**
- State validation: `_require_state(*allowed)` enforces allowed states before operations
- Argument validation: file paths, line numbers, expression strings passed through to DAP
- Build validation: checks return code and extracts DLL path from output or filesystem

**Authentication:**
- Not applicable; this is a debug adapter, not an auth service
- Environment variables (DOTNET_ROOT, NETCOREDBG_PATH, LD_LIBRARY_PATH) configure runtime discovery

**Async Coordination:**
- `asyncio.wait_for()` for timeouts on blocking operations
- `asyncio.Event` for signaling termination (`_terminated_event`)
- Background tasks (`_output_listener`, `_reader_task`) for event collection
- Task cancellation with exception handling in cleanup (`_cleanup()`)

**Kernel Workaround:**
- `_needs_strace_workaround()` in `dap_client.py` auto-detects Linux >= 6.12
- Wraps netcoredbg with `strace -f -e trace=none -o /dev/null` to prevent SIGSEGV race condition
- Can be disabled with `DEBUGGER_NET_MCP_NO_STRACE=1`

---

*Architecture analysis: 2026-02-22*
