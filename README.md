# DebuggerNetMcp

MCP server for interactive .NET debugging via ICorDebug — works on Linux kernel 6.12+ without fragile workarounds.

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│                        Claude Code (LLM)                         │
└───────────────────────────┬─────────────────────────────────────┘
                            │ MCP stdio (JSON-RPC)
┌───────────────────────────▼─────────────────────────────────────┐
│               DebuggerNetMcp.Mcp (MCP Server)                    │
│  DebuggerTools.cs — 15 [McpServerTool] methods                   │
└───────────────────────────┬─────────────────────────────────────┘
                            │ C# method calls
┌───────────────────────────▼─────────────────────────────────────┐
│             DebuggerNetMcp.Core (Debug Engine)                   │
│  DotnetDebugger.cs — ICorDebug dispatch thread + event channel   │
│  PdbReader.cs      — Portable PDB source mapping                 │
│  VariableReader.cs — ICorDebugValue recursive inspection         │
└───────────┬──────────────────────────────────────────┬──────────┘
            │ P/Invoke                                  │ COM
┌───────────▼──────────┐                  ┌────────────▼──────────┐
│  libdbgshim.so       │                  │  ICorDebug (COM)       │
│  (DbgShim 10.0.14)   │                  │  ManagedCallbackHandler│
└──────────────────────┘                  └────────────┬──────────┘
                                                       │ ICorDebug API
                                          ┌────────────▼──────────┐
                                          │   .NET Process         │
                                          │   (debuggee)           │
                                          └───────────────────────┘
```

The MCP server exposes 15 tools that drive the `DotnetDebugger` engine. The engine communicates with the .NET runtime through two channels: `libdbgshim.so` (P/Invoke, to bootstrap the debug session) and `ICorDebug` (COM, for all runtime control and inspection).

## Prerequisites

- .NET SDK 10.0 (`dotnet --version` should show `10.0.x`)
- `libdbgshim.so` from NuGet package `Microsoft.Diagnostics.DbgShim.linux-x64` (version 10.x or stable 9.x)
- Linux (kernel 6.12+ is supported via the `strace` workaround in the wrapper script)
- `strace` — required for kernel >= 6.12: `sudo apt install strace`

### Installing libdbgshim.so

`libdbgshim.so` was removed from the .NET 7+ runtime and is now distributed as a separate NuGet package.

**Option A: Download from NuGet (stable, .NET 9):**

```bash
mkdir -p /tmp/dbgshim && cd /tmp/dbgshim
dotnet new console -n tmp --no-restore
cd tmp
dotnet add package Microsoft.Diagnostics.DbgShim --version 9.0.*
cp ~/.nuget/packages/microsoft.diagnostics.dbgshim/*/runtimes/linux-x64/native/libdbgshim.so ~/.local/bin/
```

**Option B: Download nightly (for .NET 10 preview):**

The nightly feed is: `https://pkgs.dev.azure.com/dnceng/public/_packaging/dotnet-tools/nuget/v3/flat2`

Package name: `Microsoft.Diagnostics.DbgShim.linux-x64`

Download the `.nupkg`, extract it, and copy `runtimes/linux-x64/native/libdbgshim.so` to `~/.local/bin/`.

**After installing, set the environment variable:**

```bash
export DBGSHIM_PATH=~/.local/bin/libdbgshim.so
# Add to ~/.bashrc or ~/.zshrc for persistence
```

Note: `~/.local/bin/` is NOT in the default `libdbgshim.so` search path. You must set `DBGSHIM_PATH` or the server will fail to load with a `DllNotFoundException`.

## Build

```bash
./build.sh
```

This compiles:
- Native ptrace wrapper (`libdotnetdbg.so`) via CMake
- `DebuggerNetMcp.Core` and `DebuggerNetMcp.Mcp` in Release mode

The Release binary is at: `src/DebuggerNetMcp.Mcp/bin/Release/net10.0/DebuggerNetMcp.Mcp`

## Install

```bash
./install.sh
```

Registers the MCP server in Claude Code as `debugger-net` via `claude mcp add`. The wrapper script (`debugger-net-mcp.sh`) applies the `strace -f -e trace=none` workaround required for Linux kernel >= 6.12 compatibility.

Prerequisites for install:
- `DBGSHIM_PATH` env var must be set (see above)
- `claude` CLI must be in PATH

After installing, restart Claude Code to pick up the new registration. Verify with:

```bash
claude mcp list
```

## Tools

All 15 tools are exposed as MCP tools. The process must be stopped (at a breakpoint, step, or after `debug_launch`) before calling inspection tools.

### `debug_launch`

Build and launch a .NET project under the debugger. Returns `state=stopped` once the process is created and suspended at entry. Set breakpoints now (they will be activated when modules load), then call `debug_continue` to run.

**Parameters:**
- `projectPath` (string) — path to the `.csproj` file or project directory
- `appDllPath` (string) — path to the compiled `.dll` (e.g. `bin/Debug/net10.0/App.dll`)
- `firstChanceExceptions` (bool, optional) — if true, stop on every thrown exception before it is caught; default false

### `debug_attach`

Attach the debugger to a running .NET process by process ID. Returns `state=attached` once the runtime connection is established. The process continues running; use `debug_pause` to stop it for inspection.

**Parameters:**
- `processId` (number) — the process ID to attach to

### `debug_launch_test`

Launch an xUnit test project in debug mode using `VSTEST_HOST_DEBUG=1`. Attaches to the `testhost` process and returns process info. After calling this, set breakpoints with `debug_set_breakpoint` then call `debug_continue`.

**Parameters:**
- `projectPath` (string) — absolute path to the xUnit test project directory or `.csproj` file
- `filter` (string, optional) — test filter expression passed to `--filter` (e.g. `FullyQualifiedName~MyTest`)

### `debug_disconnect`

Disconnect from the debuggee and end the debug session. Resets state to idle.

### `debug_status`

Returns the current debugger state: `idle` (no session), `running` (process running), `stopped` (at breakpoint or step), or `exited` (process terminated). Also returns the server version.

### `debug_set_breakpoint`

Set a breakpoint at a source file line. Returns the breakpoint ID needed to remove it later.

**Parameters:**
- `dllPath` (string) — full path to the compiled `.dll`
- `sourceFile` (string) — source file name (e.g. `Program.cs`)
- `line` (number) — 1-based source line number

### `debug_remove_breakpoint`

Remove a previously set breakpoint by its ID.

**Parameters:**
- `breakpointId` (number) — breakpoint ID returned by `debug_set_breakpoint`

### `debug_continue`

Resume execution and wait for the next debug event (breakpoint hit, step complete, exception, or process exit). Returns the event.

### `debug_step_over`

Step over the current source line without entering called methods. Returns the resulting debug event.

### `debug_step_into`

Step into the current source line, entering any called methods. Returns the resulting debug event.

### `debug_step_out`

Step out of the current method and return to the caller. Returns the resulting debug event.

### `debug_pause`

Pause a running process. Returns `state=stopped` immediately (no event is fired — `ICorDebugController.Stop()` is synchronous).

### `debug_variables`

Get local variables at the current stopped position. Works with async state machines — hoisted locals are shown with their original names.

**Parameters:**
- `thread_id` (number, optional) — thread ID to inspect; 0 or omitted uses the current stopped thread

### `debug_stacktrace`

Get the call stack. Without `thread_id`, returns frames for ALL active threads. With `thread_id`, returns frames for that specific thread. Each frame includes source file, line number, and method name (resolved via Portable PDB).

**Parameters:**
- `thread_id` (number, optional) — thread ID to get stack for; 0 or omitted returns all active threads

### `debug_evaluate`

Evaluate a local variable name or simple expression at the current stopped position.

**Parameters:**
- `expression` (string) — variable name or simple dot-notation expression to evaluate

## Typical Debug Session

A complete workflow from launch to exit:

```
1. debug_launch(projectPath, appDllPath)
   → returns state=stopped (process suspended at entry)

2. debug_set_breakpoint(dllPath, "Program.cs", 42)
   → returns breakpoint id=1

3. debug_continue()
   → returns {type:"breakpointHit", breakpointId:1, topFrame:{file:"Program.cs", line:42}}

4. debug_variables()
   → returns [{name:"counter", type:"Int32", value:"0"}, ...]

5. debug_evaluate("counter")
   → returns {name:"counter", type:"Int32", value:"0"}

6. debug_step_over()
   → returns {type:"stopped", reason:"step", topFrame:{file:"Program.cs", line:43}}

7. debug_stacktrace()
   → returns all threads with their frame lists

8. debug_continue()
   → returns {type:"exited", exitCode:0}

9. debug_disconnect()
   → returns state=idle
```

## Running Tests

```bash
dotnet test
```

The test suite requires `DBGSHIM_PATH` to be set (same requirement as the server):

```bash
export DBGSHIM_PATH=~/.local/bin/libdbgshim.so
dotnet test
```

The test suite includes:
- **MathTests** — unit tests for the xUnit-debugging workflow (used with `debug_launch_test`)
- **PdbReaderTests** — unit tests for PDB forward/reverse lookup (no debugger required)
- **DebuggerIntegrationTests** — end-to-end: launch, breakpoint, inspect, step, exit
- **DebuggerAdvancedTests** — exceptions, multi-thread inspection, process attach

Integration tests use 30-second timeouts via `CancellationTokenSource`. HelloDebug must be built in Debug mode before running:

```bash
dotnet build tests/HelloDebug -c Debug
dotnet test
```

## Troubleshooting

### `DllNotFoundException: libdbgshim.so`

The server cannot find `libdbgshim.so`. Set `DBGSHIM_PATH`:

```bash
export DBGSHIM_PATH=~/.local/bin/libdbgshim.so
```

The default search path does NOT include `~/.local/bin/`. Even if the file exists there, it will not be found unless `DBGSHIM_PATH` is set explicitly.

### Server crashes on Linux kernel >= 6.12

The wrapper script (`debugger-net-mcp.sh`) applies the `strace -f -e trace=none -o /dev/null` workaround automatically when the server is started by Claude Code via `install.sh`.

If you are running the binary directly (not via Claude Code), prefix with strace:

```bash
strace -f -e trace=none -o /dev/null ./DebuggerNetMcp.Mcp
```

### Tests hang indefinitely

Integration tests use 30-second timeouts. If a test hangs and never times out, ensure `DBGSHIM_PATH` is set — a missing `libdbgshim.so` causes silent initialization failure in the `DotnetDebugger` constructor.

### `debug_launch_test` does not hit breakpoints

Ensure the xUnit project is built in Debug mode first:

```bash
dotnet build tests/MyTests -c Debug
```

The `VSTEST_HOST_DEBUG=1` mechanism requires the `testhost` process to wait for a debugger attach before executing test code. Release builds may have inlined or optimized away the exact IL offset the breakpoint targets.

## License

MIT
