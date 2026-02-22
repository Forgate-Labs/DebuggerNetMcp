---
phase: 03-debug-engine
plan: 03
subsystem: debug-engine
tags: [csharp, com-interop, icordebug, channels, threading, libdbgshim, strategybasedcomwrappers]

# Dependency graph
requires:
  - phase: 03-debug-engine/03-02
    provides: ManagedCallbackHandler with OnProcessCreated + OnModuleLoaded hooks
  - phase: 02-interop-engine-foundation/02-02
    provides: DbgShimInterop (KeepAlive, CreateProcessForLaunch, RegisterForRuntimeStartup, ResumeProcess, CloseResumeHandle)
  - phase: 02-interop-engine-foundation/02-02
    provides: ICorDebug, ICorDebugProcess, ICorDebugModule COM interface definitions
provides:
  - DotnetDebugger.cs — core debug engine scaffold with launch, attach, disconnect, event channel, dedicated thread
  - Channel<DebugEvent> (AllowSynchronousContinuations=false) bridging ICorDebug thread to async callers
  - Dedicated ICorDebug-Dispatch thread (not Task.Run) processing command channel
  - LaunchAsync: dotnet build -c Debug + CreateProcessForLaunch + KeepAlive-before-RegisterForRuntimeStartup
  - AttachAsync: KeepAlive + RegisterForRuntimeStartup with existing PID
  - WaitForEventAsync: async consumer for debug events
  - ResolveBreakpoint stub for Plan 04 extension
affects: [03-04-PLAN, 03-05-PLAN, DotnetDebugger]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "StrategyBasedComWrappers.GetOrCreateObjectForComInstance for [GeneratedComInterface] types — replaces Marshal.GetObjectForIUnknown (Windows-only, SYSLIB1099)"
    - "Command channel (Channel<Action>) pattern for dispatching work to dedicated ICorDebug thread"
    - "DebugThreadLoop blocking on ReadAsync().AsTask().GetAwaiter().GetResult() — intentional synchronous block on dedicated thread"

key-files:
  created:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
  modified: []

key-decisions:
  - "StrategyBasedComWrappers.GetOrCreateObjectForComInstance instead of Marshal.GetObjectForIUnknown — Marshal.GetObjectForIUnknown is Windows-only (CA1416) and returns legacy RCW incompatible with [GeneratedComInterface] source-generated types (SYSLIB1099)"
  - "ResolveBreakpoint is a regular private method stub throwing NotImplementedException — plan template used partial method but partial methods require partial class; regular stub is cleaner"
  - "ICorDebugModule.GetName uses AllocHGlobal/FreeHGlobal pattern — GetName takes IntPtr not string per SYSLIB1051 fix from Plan 02-02"

patterns-established:
  - "Pattern: StrategyBasedComWrappers for all native COM pointer → managed interface conversions (cross-platform, SYSLIB1099-free)"
  - "Pattern: Command channel dispatch — async methods WriteAsync to _commandChannel, debug thread executes via TCS, caller awaits TCS"

requirements-completed: [ENGINE-04, ENGINE-08]

# Metrics
duration: 3min
completed: 2026-02-22
---

# Phase 03 Plan 03: DotnetDebugger Core Summary

**DotnetDebugger.cs scaffold with Channel<DebugEvent>, ICorDebug-Dispatch dedicated thread, LaunchAsync (build+CreateProcessForLaunch+KeepAlive+RegisterForRuntimeStartup), AttachAsync, DisconnectAsync, and StrategyBasedComWrappers for Linux-compatible COM pointer unwrapping**

## Performance

- **Duration:** 3 min
- **Started:** 2026-02-22T23:46:58Z
- **Completed:** 2026-02-22T23:49:58Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- `DotnetDebugger.cs` created (360 lines) — complete, compilable class building clean with 0 errors and 0 warnings
- Channel<DebugEvent> with `AllowSynchronousContinuations=false` prevents deadlock on ICorDebug callback thread
- Kernel 6.12+ SIGSEGV fix baked in: `DbgShimInterop.KeepAlive(callback)` called before `RegisterForRuntimeStartup` in both LaunchAsync and AttachAsync
- Dedicated `ICorDebug-Dispatch` Thread (not Task.Run) owns all ICorDebug API calls
- `StrategyBasedComWrappers` used for native `pCordb` pointer unwrapping — eliminates Windows-only SYSLIB1099/CA1416 warnings from `Marshal.GetObjectForIUnknown`
- `ICorDebugModule.GetName` correctly uses `AllocHGlobal/FreeHGlobal` with IntPtr signature (per Plan 02-02 SYSLIB1051 fix)

## Task Commits

Each task was committed atomically:

1. **Task 1: Create DotnetDebugger.cs core — channel, thread, launch, attach, disconnect** - `d9455f0` (feat)

**Plan metadata:** *(docs commit follows)*

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - Core debug engine scaffold: Channel<DebugEvent>, command channel, ICorDebug-Dispatch thread, LaunchAsync, AttachAsync, DisconnectAsync, WaitForEventAsync, OnRuntimeStarted, OnModuleLoaded, ResolveBreakpoint stub

## Decisions Made
- **StrategyBasedComWrappers over Marshal.GetObjectForIUnknown:** The plan template used `Marshal.GetObjectForIUnknown` to unwrap `pCordb`. This causes SYSLIB1099 (casting legacy RCW to `[GeneratedComInterface]` type is unsupported) and CA1416 (Windows-only). Replaced with `new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pCordb, CreateObjectFlags.UniqueInstance)` — the correct cross-platform path for source-generated COM interop. Build is now 0 warnings.
- **Regular method stub for ResolveBreakpoint:** Plan used `partial void ResolveBreakpoint` but `DotnetDebugger` is not a `partial class`. Used a regular `private void ResolveBreakpoint(...) => throw new NotImplementedException()` stub instead. Plan 04 replaces this with the actual implementation.
- **AllocHGlobal for GetName:** `ICorDebugModule.GetName` uses the `(uint, out uint, IntPtr)` signature from the SYSLIB1051 fix in Plan 02-02. Allocated unmanaged memory with `Marshal.AllocHGlobal` and freed in finally block.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Replaced Marshal.GetObjectForIUnknown with StrategyBasedComWrappers**
- **Found during:** Task 1 (DotnetDebugger.cs creation, OnRuntimeStarted method)
- **Issue:** Plan template used `Marshal.GetObjectForIUnknown(pCordb)` cast to `ICorDebug`. This produces SYSLIB1099 (legacy RCW cast to `[GeneratedComInterface]` type is not supported on Linux) and CA1416 (API is Windows-only). On Linux this would silently fail or throw — the same class of bug fixed in Plan 03-02 for `Marshal.GetIUnknownForObject`.
- **Fix:** Replaced with `new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(pCordb, CreateObjectFlags.UniqueInstance)` — this is the correct .NET source-generated COM path that works on Linux.
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Verification:** Build exits 0 with 0 errors and 0 warnings after fix
- **Committed in:** d9455f0 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — Windows-only API, same pattern as Plan 03-02 fix)
**Impact on plan:** Essential fix for Linux compatibility. Maintains 0-warnings build discipline. No scope creep.

## Issues Encountered
- None beyond the Marshal.GetObjectForIUnknown deviation documented above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- `DotnetDebugger.cs` scaffold ready for Plan 04 to add: `SetBreakpointAsync`, `RemoveBreakpointAsync`, `ContinueAsync`, `StepOverAsync`, `StepIntoAsync`, `StepOutAsync`, `PauseAsync`
- `ResolveBreakpoint` stub in place — Plan 04 replaces the `NotImplementedException` body with `GetFunctionFromToken` + `CreateBreakpoint` + `Activate` + `BreakpointTokenToId` registration
- `_pendingBreakpoints`, `_activeBreakpoints`, `_loadedModules`, `_nextBreakpointId` fields all in place for Plan 04 use
- `BreakpointTokenToId` dictionary on `_callbackHandler` is pre-wired — Plan 04 populates it when setting breakpoints

## Self-Check: PASSED

All artifacts verified:
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` exists (360 lines)
- `03-03-SUMMARY.md` exists
- Commit `d9455f0` exists in git log
- Build: 0 errors, 0 warnings

---
*Phase: 03-debug-engine*
*Completed: 2026-02-22*
