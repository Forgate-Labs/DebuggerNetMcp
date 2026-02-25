---
phase: 03-debug-engine
plan: 02
subsystem: debug-engine
tags: [csharp, com-interop, icordebug, generatedcomclass, channels, callbacks]

# Dependency graph
requires:
  - phase: 03-debug-engine/03-01
    provides: ICorDebugClass.GetToken + IMetaDataImportMinimal COM extensions
  - phase: 02-interop-engine-foundation/02-02
    provides: All 17 ICorDebug COM interface definitions in ICorDebug.cs

provides:
  - ManagedCallbackHandler implementing all 34 ICorDebug callback methods
  - COM callback sink bridging native ICorDebug thread to Channel<DebugEvent>
  - LoadModule hook (OnModuleLoaded) for pending breakpoint resolution
  - CreateProcess hook (OnProcessCreated) giving DotnetDebugger ICorDebugProcess access
  - Token-based breakpoint ID lookup (BreakpointTokenToId dictionary)

affects: [03-03-PLAN, 03-04-PLAN, 03-05-PLAN, DotnetDebugger]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "GeneratedComClass + partial sealed class for COM vtable source generation"
    - "finally blocks on every ICorDebugManagedCallback method guaranteeing Continue(0)"
    - "Token-based breakpoint identity (methodDef uint) instead of Marshal.GetIUnknownForObject"
    - "Channel<DebugEvent> TryWrite (fire-and-forget) from COM callback thread"

key-files:
  created:
    - src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs
  modified: []

key-decisions:
  - "Token-based breakpoint ID lookup (BreakpointTokenToId[methodDef token]) instead of Marshal.GetIUnknownForObject — GetIUnknownForObject is Windows-only and incompatible with [GeneratedComInterface] source-generated proxies on Linux"
  - "BreakpointPtrToId renamed BreakpointTokenToId with key type uint (methodDef) — stable identity that works across COM callback boundaries on Linux"
  - "NameChange uses pAppDomain?.Continue(0) (null-conditional) because both params are nullable per interface definition"

patterns-established:
  - "Pattern: COM callback safety net — wrap event construction in try{} finally{ pAppDomain.Continue(0); } so debuggee never freezes on exception"
  - "Pattern: ExitProcess is the ONLY callback that must NOT call Continue — all other ICorDebugManagedCallback methods must Continue or deadlock"

requirements-completed: [ENGINE-08]

# Metrics
duration: 2min
completed: 2026-02-22
---

# Phase 3 Plan 02: ManagedCallbackHandler Summary

**[GeneratedComClass] COM callback sink with 34 methods routing ICorDebug events to Channel<DebugEvent>, with token-based breakpoint ID lookup for Linux compatibility**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-02-22T23:43:31Z
- **Completed:** 2026-02-22T23:45:11Z
- **Tasks:** 1
- **Files modified:** 1

## Accomplishments
- ManagedCallbackHandler.cs created with all 26 ICorDebugManagedCallback methods and all 8 ICorDebugManagedCallback2 methods
- Every non-ExitProcess method guards Continue(0) in a finally block — debuggee cannot freeze
- ExitProcess writes ExitedEvent + completes the ChannelWriter without calling Continue
- Fixed Windows-only Marshal.GetIUnknownForObject usage; replaced with methodDef token-based breakpoint lookup compatible with [GeneratedComInterface] source-generated proxies on Linux
- Build: 0 errors, 0 warnings

## Task Commits

Each task was committed atomically:

1. **Task 1: Create ManagedCallbackHandler.cs with all 34 callback methods** - `38fd533` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs` - [GeneratedComClass] COM sink implementing ICorDebugManagedCallback + ICorDebugManagedCallback2, 282 lines, 36 Continue(0) calls

## Decisions Made
- Used methodDef token (uint) as breakpoint identity key instead of Marshal.GetIUnknownForObject — GetIUnknownForObject is Windows-only (CA1416 + SYSLIB1099) and incompatible with [GeneratedComInterface]-generated proxy wrappers that don't wrap the same managed object across callback invocations
- NameChange signature uses nullable params (ICorDebugAppDomain?, ICorDebugThread?) per the interface declaration, and calls Continue via null-conditional

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Replaced Marshal.GetIUnknownForObject with token-based breakpoint lookup**
- **Found during:** Task 1 (ManagedCallbackHandler.cs creation)
- **Issue:** Plan template used Marshal.GetIUnknownForObject(fbp) to get a stable pointer for dictionary key — this API is Windows-only (CA1416 platform warning) and specifically incompatible with [GeneratedComInterface] source-generated COM wrappers (SYSLIB1099). On Linux it would silently fail or throw.
- **Fix:** Changed BreakpointPtrToId (Dictionary<nint, int>) to BreakpointTokenToId (Dictionary<uint, int>) keyed by methodDef token. In the Breakpoint callback, call fbp.GetFunction().GetToken() to get the token. DotnetDebugger must store the same token when registering breakpoints.
- **Files modified:** src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs
- **Verification:** Build exits 0 with 0 warnings after fix
- **Committed in:** 38fd533 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug — Windows-only API)
**Impact on plan:** Essential fix for Linux compatibility. No scope creep. DotnetDebugger (Plan 03-03) must use the same token key when registering breakpoints via BreakpointTokenToId.

## Issues Encountered
- None beyond the Marshal.GetIUnknownForObject deviation documented above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- ManagedCallbackHandler ready to be instantiated by DotnetDebugger (Plan 03-03)
- DotnetDebugger must set OnProcessCreated before calling ICorDebug.SetManagedHandler + CreateProcess
- DotnetDebugger must set OnModuleLoaded to resolve pending breakpoints on module load
- DotnetDebugger must populate BreakpointTokenToId[methodDef] when setting function breakpoints

---
*Phase: 03-debug-engine*
*Completed: 2026-02-22*
