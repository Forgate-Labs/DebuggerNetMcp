---
phase: 09-tests-documentation
plan: 02
subsystem: testing
tags: [xunit, integration-tests, ICorDebug, breakpoints, step, attach, exceptions, threads]

# Dependency graph
requires:
  - phase: 09-tests-documentation-01
    provides: DebuggerFixture + DebuggerCollection(DisableParallelization=true) infrastructure
  - phase: 08-stack-trace-and-dotnet-test
    provides: GetStackTraceAsync, GetAllThreadStackTracesAsync, AttachAsync
provides:
  - DebuggerIntegrationTests: 4 end-to-end tests for launch/breakpoint/variables/step/exit
  - DebuggerAdvancedTests: 3 end-to-end tests for unhandled exceptions, multi-thread, and attach
  - Full 14-test suite passing: 2 MathTests + 5 PdbReaderTests + 4 Integration + 3 Advanced
affects: [future-phases, regression-testing]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - WaitForSpecificEvent<T> generic helper for typed event drain
    - DrainToExit handles BreakpointHitEvent + StoppedEvent + ExceptionEvent with ContinueAsync
    - Retry-attach loop with HasExited guard for short-lived attach targets

key-files:
  created:
    - tests/DebuggerNetMcp.Tests/DebuggerIntegrationTests.cs
    - tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs
  modified: []

key-decisions:
  - "DrainToExit must handle ExceptionEvent with ContinueAsync — HelloDebug Section 21 throws unhandled exception that stops the process; without it, drain hangs until 30s timeout"
  - "Attach test uses retry loop (10 x 30ms = 300ms) instead of fixed 800ms delay — HelloDebug exits in ~215ms, so we must attach within the CLR-ready window (~50-215ms)"
  - "WaitForSpecificEvent<T> drains OutputEvent/StoppedEvent silently (no Continue), throws on ExitedEvent — prevents test hanging when unexpected exit occurs"

patterns-established:
  - "ExceptionEvent requires ContinueAsync in drain loops for programs with unhandled exceptions"
  - "Short-lived attach targets need polling loop with HasExited guard, not fixed delays"
  - "WaitForSpecificEvent<T>: throws on ExitedEvent so tests fail fast with clear error instead of timeout"

requirements-completed: [TEST-03, TEST-09]

# Metrics
duration: 7min
completed: 2026-02-23
---

# Phase 09 Plan 02: DebuggerIntegrationTests + DebuggerAdvancedTests Summary

**7 new xUnit integration tests covering launch/breakpoint/variables/step/exceptions/threads/attach — all 14 tests pass in 7 seconds**

## Performance

- **Duration:** 7 min
- **Started:** 2026-02-23T22:23:36Z
- **Completed:** 2026-02-23T22:30:42Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- 4 DebuggerIntegrationTests passing: breakpoint hit + variable inspection, step-over, step-into with stack frame assertion, natural exit (via unhandled exception drain)
- 3 DebuggerAdvancedTests passing: unhandled exception event delivery, multi-thread visibility at BP-20, process attach within short-lived target window
- Full suite: 14 tests, 0 failures, 7 second total runtime
- Tests run sequentially via `[Collection("Debugger")]` shared across both test classes

## Task Commits

Each task was committed atomically:

1. **Task 1: Write DebuggerIntegrationTests.cs — launch, breakpoint, variables, step, exit** - `d7a0b92` (feat)
2. **Task 2: Write DebuggerAdvancedTests.cs — exceptions, multi-thread, attach** - `dd8986d` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified

- `tests/DebuggerNetMcp.Tests/DebuggerIntegrationTests.cs` - 4 tests: breakpoint/variables, step-over, step-into, natural exit
- `tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs` - 3 tests: unhandled exception, multi-thread, process attach

## Decisions Made

- `DrainToExit` must handle `ExceptionEvent` with `ContinueAsync` — HelloDebug Section 21 throws an unhandled `InvalidOperationException` which stops the process in the debugger. Without calling `ContinueAsync` on `ExceptionEvent`, the drain loop hangs forever and hits the 30-second CancellationToken timeout.
- Attach test uses a retry loop (10 attempts at 30ms intervals) rather than a fixed 800ms delay — HelloDebug executes in ~215ms on this machine, making any delay >215ms a guaranteed failure. The CLR is ready for `EnumerateCLRs` around 50ms, so polling from 30ms catches the attach window reliably.
- `WaitForSpecificEvent<T>` throws `Exception` on `ExitedEvent` with a clear message — prevents tests from hanging on timeout when the process exits unexpectedly; fast-fail with meaningful error.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] DrainToExit missing ExceptionEvent handling**
- **Found during:** Task 1 (running LaunchAsync_SetBreakpoint_HitsAndInspectsVariable)
- **Issue:** DrainToExit only called ContinueAsync on BreakpointHitEvent/StoppedEvent. HelloDebug Section 21 throws an unhandled exception which stops the process with an ExceptionEvent. DrainToExit would hang forever waiting for ExitedEvent.
- **Fix:** Added `ExceptionEvent` to the ContinueAsync case in DrainToExit
- **Files modified:** tests/DebuggerNetMcp.Tests/DebuggerIntegrationTests.cs
- **Verification:** All 4 integration tests pass in 4 seconds (previously hung for 30s)
- **Committed in:** d7a0b92 (Task 1 commit)

**2. [Rule 1 - Bug] Attach test fails for short-lived HelloDebug process**
- **Found during:** Task 2 (running AttachAsync_RunningProcess_AttachesSuccessfully)
- **Issue:** Plan specified 800ms fixed delay before attaching. HelloDebug executes in ~215ms and is already dead at 800ms. `EnumerateCLRs` returns `HRESULT 0x80070057` (E_INVALIDARG — invalid PID).
- **Fix:** Replaced fixed delay with 10-retry loop (30ms sleep between attempts). Each attempt catches `InvalidOperationException` from `EnumerateCLRs`, calls `DisconnectAsync` to reset debugger state, and retries. If `target.HasExited` is true, loop exits without attach.
- **Files modified:** tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs
- **Verification:** Attach test passes reliably; test completes in 57ms
- **Committed in:** dd8986d (Task 2 commit)

---

**Total deviations:** 2 auto-fixed (both Rule 1 - Bug)
**Impact on plan:** Both fixes were necessary for tests to pass. No scope creep.

## Issues Encountered

None beyond the two auto-fixed deviations above.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Full integration test suite established: 14 tests covering all major debug scenarios
- Regression safety net in place for ICorDebug engine changes
- Phase 9 complete — all 3 plans done (infrastructure, integration tests, README)

---
*Phase: 09-tests-documentation*
*Completed: 2026-02-23*
