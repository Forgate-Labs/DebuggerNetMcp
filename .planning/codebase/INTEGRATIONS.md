# External Integrations

**Analysis Date:** 2026-02-22

## APIs & External Services

**Debug Adapter Protocol (DAP):**
- netcoredbg (Samsung/netcoredbg) - External debug adapter binary
  - SDK/Client: Custom async implementation in `src/debugger_net_mcp/dap_client.py`
  - Communication: Stdio-based JSON protocol (subprocess pipes)
  - Version: 3.1.3+ recommended
  - Discovery: Auto-detected from PATH, NETCOREDBG_PATH env var, or standard installation paths
  - Fallback paths checked: `/usr/local/bin/netcoredbg`, `/usr/local/lib/netcoredbg/netcoredbg`, `~/.local/bin/netcoredbg`, `~/.dotnet/tools/netcoredbg`

**Claude Code MCP:**
- Registered as MCP server via stdio
  - Server implementation: `src/debugger_net_mcp/server.py` using mcp.server.fastmcp.FastMCP
  - 14 debug tools exposed: debug_launch, debug_attach, debug_set_breakpoint, debug_continue, debug_step_over, debug_step_into, debug_step_out, debug_variables, debug_evaluate, debug_stacktrace, debug_breakpoints, debug_output, debug_status, debug_disconnect
  - Communication: stdio JSON-RPC via asyncio
  - Registration: `claude mcp add debugger-net --scope user`

## Data Storage

**Databases:**
- None - No persistent database integration

**File Storage:**
- Local filesystem only
  - Debug output stored in memory (deque, max 200 lines in `session.py`)
  - Project sources read from `.csproj` files and source directories
  - Build output captured to memory during `dotnet build`
  - DLL discovery from `bin/Debug/net*/` directories

**Caching:**
- In-memory session state
  - Pending breakpoints cached in `DebugSession._pending_breakpoints`
  - Thread state, variables, output held in memory during session
  - Session-level state maintained in `SessionState` enum

## Authentication & Identity

**Auth Provider:**
- None required - No external authentication
- Local process execution only
- Same-user security model (MCP runs in same context as Claude)

## Monitoring & Observability

**Error Tracking:**
- None detected - No external error tracking service

**Logs:**
- Approach: Python standard logging module
  - Logger name: `__name__` (module-specific loggers)
  - Level: INFO (set in `server.py`)
  - Output: stderr (default Python logging)
  - Key log points:
    - DAP client startup/shutdown in `dap_client.py`
    - strace workaround detection in `_needs_strace_workaround()`
    - Session state transitions in `session.py`
    - Breakpoint operations logged in session methods

## CI/CD & Deployment

**Hosting:**
- Local execution only
- Runs as subprocess of Claude Code
- No cloud deployment

**CI Pipeline:**
- No detected CI/CD integration (GitHub Actions, etc.)
- Manual testing via pytest-like scripts: `test_integration.py`, `test_dap_protocol.py`, `test_dap_raw.py`
- Build: `dotnet build` for target projects (invoked by `dotnet_utils.py`)

**Subprocess Execution:**
- netcoredbg launched as subprocess with stdio pipes
- Optional: strace wrapper on Linux >= 6.12 for race condition workaround
- .NET projects built via `dotnet build` subprocess (in `dotnet_utils.dotnet_build()`)

## Environment Configuration

**Required env vars (at MCP registration time):**
- `DOTNET_ROOT` - Path to .NET runtime (e.g., ~/.dotnet)
- `LD_LIBRARY_PATH` - Include libdbgshim.so directory (e.g., ~/.local/bin)

**Optional env vars:**
- `NETCOREDBG_PATH` - Override netcoredbg binary location
- `DEBUGGER_NET_MCP_NO_STRACE` - Set to "1" to disable strace workaround (Linux >= 6.12)

**Secrets location:**
- None - No secrets stored or used

**Configuration via .env:**
- Supported via python-dotenv import (if used)
- No .env file currently in repository
- Could be used to set NETCOREDBG_PATH, LD_LIBRARY_PATH, etc.

## System Integrations

**Operating System:**
- Platform detection via `platform.system()` for Linux-specific strace workaround
- Kernel version parsing in `_needs_strace_workaround()` for >= 6.12 detection
- Process management via `asyncio.create_subprocess_exec()` for dotnet/netcoredbg
- strace invocation as optional wrapper: `strace -f -e trace=none -o /dev/null [netcoredbg]`
- Signal handling via asyncio (SIGTERM, SIGINT for graceful shutdown)

**External Commands:**
- `dotnet build` - Invoked to compile target projects
- `netcoredbg` - Debugger binary (with optional strace wrapper)
- `strace` - Linux-only, for race condition workaround
- Command location: `shutil.which()` used for PATH lookup

## Webhooks & Callbacks

**Incoming:**
- None - No incoming webhook endpoints

**Outgoing:**
- None - No outgoing webhook calls
- All debug operations are synchronous request-response via DAP protocol

## Network

**Network Access:**
- None - No network calls made
- All operations local to machine
- netcoredbg communicates via stdio, not network

## Package/Dependency Sourcing

**Package Registry:**
- PyPI (https://pypi.org/simple)
- All dependencies resolved via uv.lock (reproducible pinned versions)
- No private package repositories

---

*Integration audit: 2026-02-22*
