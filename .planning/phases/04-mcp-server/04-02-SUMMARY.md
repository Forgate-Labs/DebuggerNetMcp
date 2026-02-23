---
phase: 04-mcp-server
plan: 02
subsystem: mcp
tags: [modelcontextprotocol, mcp, dotnet, debugger, tools, json, di]

# Dependency graph
requires:
  - phase: 03-debug-engine
    provides: DotnetDebugger public API — LaunchAsync, AttachAsync, DisconnectAsync, WaitForEventAsync, ContinueAsync, PauseAsync, StepOverAsync, StepIntoAsync, StepOutAsync, SetBreakpointAsync, RemoveBreakpointAsync, GetStackTraceAsync, GetLocalsAsync, EvaluateAsync
  - phase: 04-mcp-server/04-01
    provides: Program.cs MCP stdio host with WithTools<DebuggerTools>() forward reference and DotnetDebugger singleton DI registration
provides:
  - DebuggerTools class satisfying the forward reference in Program.cs — solution now fully buildable
  - 14 MCP tools exposing the full DotnetDebugger API over MCP stdio
  - State machine tracking idle/running/stopped/exited across tool calls
  - RunAndWait execution-control pattern (ContinueAsync/StepXxx/PauseAsync + WaitForEventAsync)
affects: [05-integration-tests, any future phase using DotnetDebugger via MCP]

# Tech tracking
tech-stack:
  added: [Microsoft.Extensions.Hosting 10.0.3]
  patterns: [McpServerToolType class pattern with constructor injection, RunAndWait async helper pattern for execution-control tools, JSON serialization of DebugEvent via anonymous object switch expression, _state machine field tracking debug session lifecycle]

key-files:
  created:
    - src/DebuggerNetMcp.Mcp/DebuggerTools.cs
  modified:
    - src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj

key-decisions:
  - "Microsoft.Extensions.Hosting 10.0.3 added explicitly — was missing as direct dependency; only Abstractions was transitive from ModelContextProtocol"
  - "RunAndWait helper captures ct in lambda and serializes DebugEvent inline — avoids per-tool boilerplate for 5 execution-control tools"
  - "_state field is private and managed only within DebuggerTools — DotnetDebugger has no concept of state, only per-operation results"
  - "debug_status reads _state directly without calling DotnetDebugger — zero-latency state query, no COM thread dispatch"

patterns-established:
  - "McpServerToolType pattern: [McpServerToolType] on class + [McpServerTool(Name=...)] on every method + [Description] on method and non-CancellationToken params"
  - "Execution-control pattern: RunAndWait(Func<Task>, CancellationToken) → operation + WaitForEventAsync → serialize DebugEvent"
  - "Inspection error pattern: catch Exception → check HRESULT hint for 80131301 → structured JSON with success=false + error message"

requirements-completed: [MCP-02]

# Metrics
duration: 2min
completed: 2026-02-23
---

# Phase 4 Plan 02: DebuggerTools MCP Tool Class Summary

**[McpServerToolType] DebuggerTools with 14 tools wrapping DotnetDebugger: session management (launch, attach, disconnect, status), breakpoints (set, remove), execution control via RunAndWait+WaitForEventAsync (continue, step_over, step_into, step_out, pause), and inspection (variables, stacktrace, evaluate)**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-23T00:23:00Z
- **Completed:** 2026-02-23T00:24:37Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- Created DebuggerTools.cs with all 14 MCP tools, satisfying the forward reference in Program.cs — solution now builds clean (0 errors, 0 warnings across all 4 projects)
- Implemented RunAndWait helper that composes any execution-control operation with WaitForEventAsync, returning the serialized DebugEvent in the response
- Added _state machine (idle/running/stopped/exited) tracking session lifecycle across tool calls; debug_status reads it directly without COM thread dispatch
- Fixed blocking pre-existing issue: Microsoft.Extensions.Hosting was missing as direct dependency, causing Host.CreateApplicationBuilder CS0103 error in Program.cs

## Task Commits

Each task was committed atomically:

1. **Task 1: Create DebuggerTools.cs with all 14 MCP tool methods** - `05f9f96` (feat)

**Plan metadata:** (docs commit below)

## Files Created/Modified
- `src/DebuggerNetMcp.Mcp/DebuggerTools.cs` - [McpServerToolType] class with 14 tool methods, RunAndWait helper, SerializeEvent switch expression, and _state machine
- `src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj` - Added Microsoft.Extensions.Hosting 10.0.3 PackageReference

## Decisions Made
- Microsoft.Extensions.Hosting added explicitly: Program.cs uses `Host.CreateApplicationBuilder()` from the full Hosting package; only `Hosting.Abstractions` was coming in transitively from ModelContextProtocol
- RunAndWait captures `ct` in the lambda (`() => debugger.ContinueAsync(ct)`) — the same token is used for both the operation and WaitForEventAsync, ensuring consistent cancellation
- `_state` is DebuggerTools-owned state, not DotnetDebugger state — correct architectural separation since DotnetDebugger is stateless from a session perspective

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added Microsoft.Extensions.Hosting 10.0.3 direct package dependency**
- **Found during:** Task 1 (first build attempt)
- **Issue:** `Host.CreateApplicationBuilder` in Program.cs caused CS0103 error — `Microsoft.Extensions.Hosting` was not in the project's dependencies; only `Microsoft.Extensions.Hosting.Abstractions` was transitively available via ModelContextProtocol
- **Fix:** Ran `dotnet add package Microsoft.Extensions.Hosting` which resolved to 10.0.3
- **Files modified:** `src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj`
- **Verification:** Build succeeded 0 errors after package add
- **Committed in:** `05f9f96` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 blocking)
**Impact on plan:** The missing package was a pre-existing gap from Plan 01 (Program.cs was written when the project was intentionally unbuildable). Required to complete the task. No scope creep.

## Issues Encountered

None beyond the auto-fixed blocking issue above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness
- Full solution builds clean: 0 errors, 0 warnings across DebuggerNetMcp.Core, DebuggerNetMcp.Mcp, HelloDebug, DebuggerNetMcp.Tests
- All 14 MCP tools are registered and reachable via MCP stdio protocol
- Phase 4 is complete — the MCP server is fully functional
- Phase 5 (integration tests) can now call the MCP tools against HelloDebug

---
*Phase: 04-mcp-server*
*Completed: 2026-02-23*
