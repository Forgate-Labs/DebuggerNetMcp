---
phase: 10-corrija-os-debitos-tecnicos-todos-que-ficaram-em-aberto
plan: "02"
subsystem: testing
tags: [xunit, iCorDebug, vstest, testhost, debug-attach, integration-tests]

# Dependency graph
requires:
  - phase: 09-02
    provides: integration test suite with DebuggerAdvancedTests (THRD-01, THRD-02, ATTACH-01)
provides:
  - THRD-03 automated (PauseAsync suspends all threads)
  - DTEST-02 automated (LaunchTestAsync breakpoint in Fact method)
  - DebuggerTestHelpers shared helper class (WaitForSpecificEvent, DrainToExit)
  - Multi-session ICorDebug stability fixes (session ID tracking, testhost cleanup, Break callback)
affects: [future test additions, LaunchTestAsync users]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Session ID tracking in ManagedCallbackHandler to prevent stale ExitProcess from closing new session channel"
    - "VSTEST_DEBUG_NOBP=1 to suppress Debugger.Break() in testhost after spin-loop exits"
    - "Explicit testhost PID kill in DisconnectAsync — testhost is NOT in Linux process tree"
    - "DebuggerTestHelpers static class as single source of truth for test helper methods"

key-files:
  created:
    - tests/DebuggerNetMcp.Tests/DebuggerTestHelpers.cs
  modified:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs
    - tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs

key-decisions:
  - "VSTEST_DEBUG_NOBP=1 suppresses Debugger.Break() in testhost — root cause of 20% LaunchTestAsync flake rate"
  - "Session ID (BeginNewSession/_currentSessionId/_processSessionId) prevents stale ExitProcess from closing new session channel"
  - "_testhostPid tracked and killed explicitly in DisconnectAsync — not in Linux process tree of _dotnetTestProcess"
  - "lock(_bpLock) added around DisconnectAsync module/pending-BP clear to prevent TOCTOU with OnModuleLoaded callback thread"
  - "proc.Continue(0) removed from OnRuntimeStarted launch path — CreateProcess callback owns Continue semantics"

patterns-established:
  - "DebuggerTestHelpers: shared static helpers for test event draining, single definition in project"
  - "LaunchTestAsync: VSTEST_DEBUG_NOBP=1 required alongside VSTEST_HOST_DEBUG=1 to prevent Break-callback halt"

requirements-completed: [THRD-03, DTEST-02, TEST-03, TEST-09]

# Metrics
duration: ~3h
completed: 2026-02-24
---

# Phase 10 Plan 02: Test Infrastructure Consolidation and Advanced Test Coverage Summary

**Two new automated tests (THRD-03 PauseAsync, DTEST-02 LaunchTestAsync) added with 30/30 stability, fixing a root-cause Break-callback bug that caused 20% flake rate in testhost attach**

## Performance

- **Duration:** ~3 hours (debugging intermittent failure)
- **Started:** 2026-02-24T21:10:00Z
- **Completed:** 2026-02-24T23:00:00Z
- **Tasks:** 2 of 2
- **Files modified:** 4

## Accomplishments

- Extracted `WaitForSpecificEvent<T>` and `DrainToExit` from two test classes into `DebuggerTestHelpers.cs` — single source of truth, no more copy-paste drift
- Added `PauseAsync_SuspendsAllThreads_NoEventAfterPause` (THRD-03) and `LaunchTestAsync_BreakpointInFact_HitsWithLocals` (DTEST-02)
- Identified and fixed root cause of ~20% intermittent failure: testhost calls `Debugger.Break()` after its spin-loop exits; our `Break` ICorDebug callback wrote `StoppedEvent("pause")` and did NOT call Continue, leaving the testhost suspended indefinitely — fixed with `VSTEST_DEBUG_NOBP=1`
- Fixed 4 additional multi-session ICorDebug bugs discovered during investigation: session ID channel guard, stale `BreakpointTokenToId`, testhost process leak, missing lock in `DisconnectAsync`
- 16/16 tests pass consistently across 30+ consecutive full-suite runs

## Task Commits

1. **Task 1: Extract shared helpers into DebuggerTestHelpers.cs** - `55f60fc` (refactor)
2. **Task 2: Add PauseAsync and LaunchTestAsync tests + multi-session fixes** - `083d4ec` (feat)

## Files Created/Modified

- `tests/DebuggerNetMcp.Tests/DebuggerTestHelpers.cs` — Created: `WaitForSpecificEvent<T>` and `DrainToExit` shared helpers
- `tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs` — Added THRD-03 and DTEST-02 test methods; updated call sites to use `DebuggerTestHelpers.` prefix
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — Session cleanup fixes, `VSTEST_DEBUG_NOBP=1`, stdout drain, `_testhostPid` explicit kill, removed `proc.Continue(0)` from launch path, `lock(_bpLock)` in `DisconnectAsync`
- `src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs` — `ClearBreakpointRegistry()`, session ID tracking (`BeginNewSession`, `_currentSessionId`, `_processSessionId`)

## Decisions Made

- **VSTEST_DEBUG_NOBP=1**: testhost (`WaitForDebugger` method) calls `Debugger.Break()` after its spin-loop exits. ICorDebug delivers this as a `Break` stopping callback, which our handler converts to `StoppedEvent("pause")` without calling Continue. Setting `VSTEST_DEBUG_NOBP=1` suppresses this `Break()` call entirely, allowing the testhost to proceed to load test assemblies.

- **Session ID tracking**: A stale `ExitProcess` callback from a prior attach session could call `TryComplete()` on the new session's channel. Session IDs captured at `CreateProcess` and validated at `ExitProcess` prevent this. `SuppressExitProcess` flag handles the case where `DisconnectAsync` terminates the process intentionally.

- **Explicit testhost PID kill**: On Linux, `Kill(entireProcessTree: true)` on `_dotnetTestProcess` (the vstest runner) does NOT kill the testhost — they have different Linux parent PIDs. Storing `_testhostPid` and killing explicitly in `DisconnectAsync` prevents testhost process accumulation across test runs.

- **Removed `proc.Continue(0)` from OnRuntimeStarted launch path**: This was causing `0x8013132F` (CORDBG_E_PROCESS_NOT_SYNCHRONIZED) when `StopAtCreateProcess=true`. The `CreateProcess` callback already owns Continue semantics for both paths.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Removed proc.Continue(0) from OnRuntimeStarted launch path**
- **Found during:** Task 2 (investigating intermittent failures)
- **Issue:** Calling `proc.Continue(0)` in `OnRuntimeStarted` when `StopAtCreateProcess=true` caused `0x8013132F` because `CreateProcess` callback had already handled the stop; calling Continue again desynchronized ICorDebug state
- **Fix:** Removed `proc.Continue(0)` from launch path; `CreateProcess` callback owns Continue semantics
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Committed in:** `083d4ec`

**2. [Rule 1 - Bug] Added lock(_bpLock) around DisconnectAsync module/BP clear**
- **Found during:** Task 2 (code review during investigation)
- **Issue:** `DisconnectAsync` cleared `_loadedModules` and `_pendingBreakpoints` without the `_bpLock` lock held, creating a TOCTOU race with `OnModuleLoaded` running on ICorDebug callback thread
- **Fix:** Wrapped both clears in `lock(_bpLock)` in `DisconnectAsync`
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Committed in:** `083d4ec`

**3. [Rule 1 - Bug] Added session ID tracking to prevent stale ExitProcess from closing new session channel**
- **Found during:** Task 2 (multi-session test failures)
- **Issue:** `ExitProcess` from a prior process could call `TryComplete()` on the new session's event channel, causing premature termination
- **Fix:** `BeginNewSession()` increments `_currentSessionId`; `CreateProcess` captures it as `_processSessionId`; `ExitProcess` guards with `_processSessionId != _currentSessionId` check
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs`
- **Committed in:** `083d4ec`

**4. [Rule 1 - Bug] Added ClearBreakpointRegistry() call in DisconnectAsync**
- **Found during:** Task 2 (stale breakpoint token mappings across sessions)
- **Issue:** `BreakpointTokenToId` dictionary in `ManagedCallbackHandler` was never cleared between sessions, causing wrong breakpoint IDs to be reported in subsequent sessions
- **Fix:** Added `ClearBreakpointRegistry()` method and call in `DisconnectAsync`
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs`, `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Committed in:** `083d4ec`

**5. [Rule 1 - Bug] Tracked _testhostPid and killed explicitly in DisconnectAsync**
- **Found during:** Task 2 (orphaned testhost processes accumulating)
- **Issue:** `Kill(entireProcessTree: true)` on `_dotnetTestProcess` did not kill testhost on Linux (different process tree parent); orphaned testhosts consumed resources and could interfere with subsequent test runs
- **Fix:** Added `_testhostPid` field, stored at `LaunchTestAsync`, killed explicitly in `DisconnectAsync`
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Committed in:** `083d4ec`

**6. [Rule 1 - Bug] Set VSTEST_DEBUG_NOBP=1 to fix LaunchTestAsync 20% flake**
- **Found during:** Task 2 (root cause analysis of intermittent CT timeout)
- **Issue:** Testhost calls `Debugger.Break()` after spin-loop exits; ICorDebug delivers `Break` callback which our handler converts to `StoppedEvent("pause")` without calling Continue, leaving testhost suspended, unable to load test assemblies
- **Fix:** Set `VSTEST_DEBUG_NOBP=1` in `LaunchTestAsync` environment to suppress `Debugger.Break()` call
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Committed in:** `083d4ec`

---

**Total deviations:** 6 auto-fixed (all Rule 1 - Bug)
**Impact on plan:** All fixes necessary for test stability and correctness. No scope creep — all bugs directly caused current task to fail or produce wrong results.

## Issues Encountered

- `LaunchTestAsync_BreakpointInFact_HitsWithLocals` had a ~20% intermittent failure rate. Investigation required: adding timestamped diagnostic logging to `DotnetDebugger.cs`, running 30+ iterations to capture failures, and analyzing ICorDebug callback timing to identify the root cause. The 1-second `DebugActiveProcess` delay was a symptom (CLR reaching safe point), not the cause. The actual cause was the `Debugger.Break()` call in testhost after spin-loop exit creating a stopping event that nobody resumed.

## Next Phase Readiness

- 16 tests covering launch, breakpoints, stepping, locals inspection, multi-thread, exception, pause, attach, and LaunchTestAsync — full regression coverage for v1.1 tech debt cleanup
- All ICorDebug multi-session bugs resolved; debugger is stable across sequential test runs
- Ready for remaining Phase 10 plans if any

---
*Phase: 10-corrija-os-debitos-tecnicos-todos-que-ficaram-em-aberto*
*Completed: 2026-02-24*
