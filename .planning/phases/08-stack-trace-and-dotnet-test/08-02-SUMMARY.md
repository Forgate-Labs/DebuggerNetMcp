---
phase: 08-stack-trace-and-dotnet-test
plan: 02
subsystem: testing
tags: [xunit, vstest, dotnet-test, VSTEST_HOST_DEBUG, attach, LaunchTestAsync]

# Dependency graph
requires:
  - phase: 08-stack-trace-and-dotnet-test-01
    provides: AttachAsync infrastructure reused by LaunchTestAsync
  - phase: 07-exceptions-threading-attach
    provides: AttachAsync implementation + DisconnectAsync pattern
provides:
  - LaunchTestAsync in DotnetDebugger (builds project, starts dotnet test with VSTEST_HOST_DEBUG=1, parses testhost PID, calls AttachAsync)
  - _dotnetTestProcess field + DisconnectAsync cleanup (kills vstest runner on disconnect)
  - debug_launch_test MCP tool (projectPath + optional filter, returns state="attached")
  - MathTests xUnit class with BP-DTEST markers for breakpoint verification
affects: [future-test-debugging, documentation]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "VSTEST_HOST_DEBUG=1 pattern: set env var before dotnet test, parse 'Process Id: NNN' from stdout, attach via existing AttachAsync"
    - "vstest runner cleanup: _dotnetTestProcess field tracks parent process; DisconnectAsync kills it to prevent hanging"
    - "LaunchTestAsync follows same cleanup-first pattern as LaunchAsync (calls DisconnectAsync at start)"

key-files:
  created:
    - tests/DebuggerNetMcp.Tests/UnitTest1.cs (replaced with MathTests class)
  modified:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Mcp/DebuggerTools.cs

key-decisions:
  - "LaunchTestAsync reuses AttachAsync directly — no code duplication; all ICorDebug attach infrastructure is shared"
  - "25-second timeout for testhost PID from stdout — generous for slow CI machines but bounded"
  - "DisconnectAsync kills _dotnetTestProcess inside DispatchAsync lambda to keep all session state changes on debug thread"
  - "ServerVersion bumped to 0.9.0 for debug_launch_test feature addition"

patterns-established:
  - "VSTEST_HOST_DEBUG flow: LaunchTestAsync = build + spawn dotnet test + parse PID + AttachAsync"
  - "Process tracking: _dotnetTestProcess field; cleaned up in DisconnectAsync alongside _process"

requirements-completed: [DTEST-01, DTEST-02]

# Metrics
duration: 2min
completed: 2026-02-23
---

# Phase 8 Plan 02: debug_launch_test — xUnit Debugger Integration Summary

**LaunchTestAsync + debug_launch_test MCP tool enabling breakpoint debugging of xUnit [Fact] methods via VSTEST_HOST_DEBUG=1 attach pattern**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-23T21:54:55Z
- **Completed:** 2026-02-23T21:56:36Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- Added `LaunchTestAsync` to `DotnetDebugger`: builds project with `dotnet build`, starts `dotnet test` with `VSTEST_HOST_DEBUG=1`, parses testhost PID from stdout using `Regex.Match(@"Process Id:\s*(\d+)")`, calls `AttachAsync(testhostPid)`
- Added `_dotnetTestProcess` field and `DisconnectAsync` cleanup: kills the vstest runner process on disconnect to prevent hanging `dotnet test`
- Added `debug_launch_test` MCP tool: accepts `projectPath` + optional `filter`, returns `{success, state:"attached", pid, processName, note}`
- Replaced `UnitTest1.cs` with `MathTests` class: two `[Fact]` methods with meaningful local variables (`a=21`, `b=21`, `result`, `label`) and `BP-DTEST` markers for breakpoint verification
- Both xUnit tests pass (21+21=42, 6*7=42)

## Task Commits

Each task was committed atomically:

1. **Task 1: Add LaunchTestAsync + _dotnetTestProcess field + DisconnectAsync cleanup** - `4f97b3d` (feat)
2. **Task 2: Add debug_launch_test MCP tool + debuggable MathTests xUnit class** - `4ce669f` (feat)

**Plan metadata:** (docs commit — see below)

## Files Created/Modified

- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — Added `_dotnetTestProcess` field, `LaunchTestAsync` method, `DisconnectAsync` cleanup for vstest runner
- `src/DebuggerNetMcp.Mcp/DebuggerTools.cs` — Added `debug_launch_test` tool, bumped `ServerVersion` to `0.9.0`
- `tests/DebuggerNetMcp.Tests/UnitTest1.cs` — Replaced with `MathTests` class with `BP-DTEST` markers and passing assertions

## Decisions Made

- `LaunchTestAsync` calls `AttachAsync` directly — reuses all existing ICorDebug attach infrastructure with zero duplication.
- 25-second timeout for reading the testhost PID from stdout — bounded but generous for slow machines.
- `_dotnetTestProcess` cleanup placed inside `DispatchAsync` lambda in `DisconnectAsync` to keep session state changes on the debug thread (consistent with all other state cleanup).
- `ServerVersion` bumped to `0.9.0` per MEMORY.md rule: "sempre incrementar versão ao mudar qualquer um dos dois projetos."

## Deviations from Plan

None — plan executed exactly as written. The `_debugger` reference in the plan's pseudocode was trivially corrected to `debugger` (primary constructor parameter name) — not a deviation, just adapting pseudocode to actual code pattern.

## Issues Encountered

None.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- `debug_launch_test` is ready for use: set breakpoint in a `[Fact]` method, call `debug_launch_test`, call `debug_continue`, inspect variables
- Manual smoke test: run `debug_launch_test` with `tests/DebuggerNetMcp.Tests/`, set BP on line 11 (`int result = a + b;`) in `UnitTest1.cs`, continue — should hit breakpoint with `a=21, b=21` visible
- Phase 8 is now complete (both plans done)
- Phase 9 can begin

---
*Phase: 08-stack-trace-and-dotnet-test*
*Completed: 2026-02-23*
