---
phase: 03-debug-engine
plan: 04
subsystem: debug-engine
tags: [csharp, icordebug, breakpoints, stepping, com-interop, pdb]

# Dependency graph
requires:
  - phase: 03-debug-engine/03-03
    provides: DotnetDebugger.cs scaffold with ResolveBreakpoint stub, _pendingBreakpoints, _activeBreakpoints, _loadedModules
  - phase: 03-debug-engine/03-02
    provides: ManagedCallbackHandler with BreakpointTokenToId[uint methodDef] dictionary
  - phase: 02-interop-engine-foundation/02-03
    provides: PdbReader.FindLocation(dllPath, sourceFile, line) returning (methodToken, ilOffset)
  - phase: 02-interop-engine-foundation/02-02
    provides: ICorDebugModule.GetFunctionFromToken, ICorDebugFunction.CreateBreakpoint, ICorDebugStepper
provides:
  - SetBreakpointAsync: PdbReader.FindLocation → CreateBreakpoint+Activate(1) if module loaded; PendingBreakpoint if not
  - RemoveBreakpointAsync: Activate(0) + removes from _activeBreakpoints + _pendingBreakpoints
  - ResolveBreakpoint: full impl replacing NotImplementedException stub
  - ContinueAsync: _process.Continue(0) dispatched to debug thread
  - PauseAsync: _process.Stop(0) dispatched to debug thread
  - StepOverAsync / StepIntoAsync: ICorDebugStepper + INTERCEPT_NONE + STOP_NONE + Step(0/1) + Continue
  - StepOutAsync: ICorDebugStepper + INTERCEPT_NONE + STOP_NONE + StepOut() + Continue
  - GetCurrentThread: ICorDebugThreadEnum.Next(1, ...) helper
  - ICorDebugThreadEnum: Next + Skip + Reset + Clone + GetCount methods added
affects: [03-05-PLAN, DotnetDebugger]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "PendingBreakpoint queue pattern: SetBreakpointAsync queues when module not yet loaded; OnModuleLoaded resolves pending via ResolveBreakpoint"
    - "BreakpointTokenToId[uint methodDef] as stable key: avoids Marshal.GetIUnknownForObject (Windows-only) and COM proxy identity instability"
    - "ICorDebugStepper invariants: INTERCEPT_NONE + STOP_NONE (not STOP_UNMANAGED) + Continue AFTER step setup"

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Interop/ICorDebug.cs

key-decisions:
  - "PdbReader.FindLocation throws on miss (not null) — used try/catch instead of null check from plan template"
  - "Module lookup uses _loadedModules dictionary directly (not GetName per-module) — avoids AllocHGlobal in every lookup iteration"
  - "BreakpointTokenToId keyed by uint methodDef (not Marshal.GetIUnknownForObject nint) — per STATE.md decision from Plan 03-02"
  - "ICorDebugThreadEnum stub extended with 5 methods (Skip/Reset/Clone/GetCount/Next) — ICorDebugEnum pattern from cordebug.idl vtable"

requirements-completed: [ENGINE-05, ENGINE-06]

# Metrics
duration: 2min
completed: 2026-02-22
---

# Phase 03 Plan 04: Execution Control + Breakpoint Management Summary

**SetBreakpointAsync (PdbReader.FindLocation → CreateBreakpoint + Activate), RemoveBreakpointAsync, ResolveBreakpoint, ContinueAsync, PauseAsync, StepOverAsync, StepIntoAsync, StepOutAsync added to DotnetDebugger.cs; ICorDebugThreadEnum extended with Next/Skip/Reset/Clone/GetCount; clean build 0 errors 0 warnings**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-22T23:52:35Z
- **Completed:** 2026-02-22T23:54:22Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- `ResolveBreakpoint` full implementation: `GetFunctionFromToken` → `CreateBreakpoint` → `Activate(1)` → registers `BreakpointTokenToId[methodDef]`
- `SetBreakpointAsync`: calls `PdbReader.FindLocation`, looks up loaded module by dll name, either resolves immediately or queues as `PendingBreakpoint`
- `RemoveBreakpointAsync`: `Activate(0)` + removes from both `_activeBreakpoints` and `_pendingBreakpoints`
- `ContinueAsync` / `PauseAsync`: minimal delegation to `_process.Continue(0)` / `_process.Stop(0)` via `DispatchAsync`
- `StepOverAsync` / `StepIntoAsync` / `StepOutAsync`: `ICorDebugStepper` with `INTERCEPT_NONE` + `STOP_NONE` (not `STOP_UNMANAGED`) — per research invariants
- `GetCurrentThread`: uses `ICorDebugThreadEnum.Next(1, threads, out uint fetched)` helper
- `ICorDebugThreadEnum` in `ICorDebug.cs` extended with 5 methods matching ICorDebugEnum pattern from cordebug.idl
- Build: 0 errors, 0 warnings

## Task Commits

Each task was committed atomically:

1. **Task 1 + Task 2: Breakpoint management + execution control** — `3758b32` (feat) — both tasks touch the same file and were committed together after both verified

**Plan metadata:** *(docs commit follows)*

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — Added: ResolveBreakpoint (full), SetBreakpointAsync, RemoveBreakpointAsync, ContinueAsync, PauseAsync, StepOverAsync, StepIntoAsync, StepOutAsync, GetCurrentThread, StepAsync (private helper). Removed: #pragma warning CS0414 (field now used)
- `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` — ICorDebugThreadEnum extended with Skip, Reset, Clone, GetCount, Next methods

## Decisions Made
- **PdbReader.FindLocation throws, does not return null:** The plan template had `if (location is null)` but `FindLocation` returns a non-nullable value tuple `(int, int)` and throws `InvalidOperationException` on miss. Used `try/catch` wrapping instead.
- **Module lookup via _loadedModules dictionary:** Plan template iterated modules calling `m.GetName(...)` on each. Instead, `_loadedModules` is already keyed by full module path (from `OnModuleLoaded`). Looked up by `EndsWith(dllName, OrdinalIgnoreCase)` on the dictionary keys — avoids repeated `AllocHGlobal` calls.
- **BreakpointTokenToId[uint methodDef]:** Used `uint methodToken` as the dictionary key matching `ManagedCallbackHandler.BreakpointTokenToId`. Plan template used `Marshal.GetIUnknownForObject` (Windows-only, incompatible with source-generated COM proxies) — pre-existing STATE.md decision applied.
- **ICorDebugThreadEnum methods:** Added 5 methods (ICorDebugEnum pattern) so `GetCurrentThread` compiles. Only `Next` is called at runtime.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] PdbReader.FindLocation returns non-nullable tuple — null check replaced with try/catch**
- **Found during:** Task 1 (SetBreakpointAsync)
- **Issue:** Plan template used `var location = PdbReader.FindLocation(...); if (location is null)` — but `FindLocation` is declared `(int, int)` (non-nullable value tuple) and throws `InvalidOperationException` on miss. The null check would cause CS0183 warning and never fire.
- **Fix:** Wrapped in `try { (methodToken, ilOffset) = PdbReader.FindLocation(...); } catch (Exception ex) { tcs.SetException(...); return; }` — correctly handles the throw path.
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs`
- **Commit:** 3758b32

**2. [Rule 3 - Blocking] ICorDebugThreadEnum had no methods — added Next + ICorDebugEnum pattern**
- **Found during:** Task 2 (GetCurrentThread implementation)
- **Issue:** `ICorDebugThreadEnum` stub interface had no methods. Calling `threadEnum.Next(...)` would not compile.
- **Fix:** Added `Skip`, `Reset`, `Clone`, `GetCount`, `Next` — the standard ICorDebugEnum pattern from cordebug.idl. `Next` takes `(uint celt, ICorDebugThread[] objects, out uint pceltFetched)`.
- **Files modified:** `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs`
- **Commit:** 3758b32

---

**Total deviations:** 2 auto-fixed (1 bug — null check on non-nullable type; 1 blocking — missing interface methods)
**Impact on plan:** Essential correctness fixes. No scope creep.

## Issues Encountered
None beyond the two deviations documented above.

## User Setup Required
None.

## Next Phase Readiness
- `DotnetDebugger` now has full execution control + breakpoint management
- Plan 05 can add `GetStackFramesAsync`, `GetVariablesAsync`, `EvaluateExpressionAsync` using existing `_process` + `VariableReader`
- `BreakpointHitEvent` and `StoppedEvent("step")` are already fired by `ManagedCallbackHandler` — Plan 05 consumers can await them via `WaitForEventAsync`

## Self-Check: PASSED

All artifacts verified:
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` modified (SetBreakpointAsync, RemoveBreakpointAsync, ResolveBreakpoint, ContinueAsync, PauseAsync, StepOverAsync, StepIntoAsync, StepOutAsync present)
- `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` modified (ICorDebugThreadEnum.Next present)
- `03-04-SUMMARY.md` exists
- Commit `3758b32` exists in git log
- Build: 0 errors, 0 warnings

---
*Phase: 03-debug-engine*
*Completed: 2026-02-22*
