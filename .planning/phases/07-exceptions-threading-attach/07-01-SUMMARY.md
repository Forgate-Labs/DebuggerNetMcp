---
phase: 07-exceptions-threading-attach
plan: 01
subsystem: debug-engine
tags: [icordebug, exceptions, first-chance, unhandled, callback, managed-callback]

# Dependency graph
requires:
  - phase: 06-closures-iterators-object-graph
    provides: VariableReader PE metadata helpers (GetTypeName, GetModulePath, ReadInstanceFieldsFromPE, GetBaseTypeToken) used to read exception type name and message

provides:
  - Real exception type name and message in ExceptionEvent (DivideByZeroException, NullReferenceException, etc.)
  - Double-reporting guard: _exceptionStopPending prevents v1 and v2 callbacks both writing unhandled ExceptionEvent
  - First-chance exception notification opt-in via debug_launch firstChanceExceptions parameter
  - NotifyFirstChanceExceptions flag threaded from debug_launch through DotnetDebugger.LaunchAsync to ManagedCallbackHandler

affects: [08-threading, 09-attach, any phase testing exception handling]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - v1 Exception callback owns unhandled exception handling; sets _exceptionStopPending flag
    - v2 Exception callback defers to v1 guard; only fires as fallback if v1 did not run
    - TryReadExceptionInfo reads exception object via ICorDebugThread.GetCurrentException + PE metadata walk
    - TryReadStringField walks inheritance chain for _message field using VariableReader.ReadInstanceFieldsFromPE

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Engine/VariableReader.cs
    - src/DebuggerNetMcp.Mcp/DebuggerTools.cs

key-decisions:
  - "v1 Exception callback is authoritative for unhandled exception events; v2 continues silently when _exceptionStopPending is true"
  - "TryReadStringField calls GetClass() fresh on objVal each call to get the correct level's class for GetFieldValue"
  - "GetTypeName, GetModulePath, ReadInstanceFieldsFromPE, GetBaseTypeToken changed from private to internal static in VariableReader so ManagedCallbackHandler can reuse them"
  - "First-chance exception support is opt-in via debug_launch firstChanceExceptions=true; default false to avoid noise"
  - "NotifyFirstChanceExceptions reset to false in DisconnectAsync to prevent stale state across sessions"

patterns-established:
  - "Exception callback pattern: v1 owns unhandled stop + sets pending flag; v2 checks flag and defers"
  - "TryReadExceptionInfo: GetCurrentException -> Dereference -> GetClass -> PE metadata for type name + _message field walk"

requirements-completed: [EXCP-01, EXCP-02]

# Metrics
duration: 20min
completed: 2026-02-23
---

# Phase 7 Plan 01: Exception Event Improvements Summary

**Real exception type name and message in ExceptionEvent via ICorDebugThread.GetCurrentException + PE metadata walk, with double-reporting guard and first-chance notification opt-in**

## Performance

- **Duration:** ~20 min
- **Started:** 2026-02-23T14:00:00Z
- **Completed:** 2026-02-23T14:15:18Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments

- ExceptionEvent now carries real exception type (e.g. "DivideByZeroException") and message (e.g. "Attempted to divide by zero.") instead of "<unhandled>" and "Unhandled exception"
- Double-reporting guard: _exceptionStopPending flag prevents both v1 and v2 callbacks from writing stopping ExceptionEvent for the same unhandled exception
- First-chance exception opt-in: debug_launch accepts firstChanceExceptions=true to stop on every thrown exception before it is caught
- VariableReader helper methods changed to internal static so ManagedCallbackHandler can reuse PE metadata reading without duplicating code

## Task Commits

Each task was committed atomically:

1. **Task 1: TryReadExceptionInfo + double-reporting guard in ManagedCallbackHandler** - `6b95046` (feat)
2. **Task 2: Thread notifyFirstChanceExceptions through DotnetDebugger.LaunchAsync and debug_launch** - `53edffb` (feat)

## Files Created/Modified

- `src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs` - Added NotifyFirstChanceExceptions property, _exceptionStopPending field, TryReadExceptionInfo and TryReadStringField helpers; replaced v1 and v2 Exception callbacks
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - Added notifyFirstChanceExceptions param to LaunchAsync, set on callbackHandler before launch, reset in DisconnectAsync
- `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` - Changed GetTypeName, GetModulePath, ReadInstanceFieldsFromPE, GetBaseTypeToken from private to internal static
- `src/DebuggerNetMcp.Mcp/DebuggerTools.cs` - Added firstChanceExceptions optional bool param to debug_launch; updated description

## Decisions Made

- v1 Exception callback is authoritative for unhandled exception events (v2 defers via _exceptionStopPending guard). This prevents double-writing to the event channel which would cause a second spurious stopped event.
- TryReadStringField calls `objVal.GetClass()` fresh inside the method for each inheritance level's GetFieldValue call, matching the pattern used successfully in VariableReader.ReadObjectFields.
- Plan noted `GetClass_()` and `GetString_()` trailing-underscore names as possible COM interop method names — verified in ICorDebug.cs that actual names are `GetClass()` and `GetString()` without underscore. Used actual names.
- First-chance exceptions are silently continued by default; opt-in avoids flooding event channel for apps that use exceptions for control flow (JSON parsing, IO, etc.).

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] TryReadStringField uses fresh GetClass() call instead of passed-in class token**

- **Found during:** Task 1 (TryReadExceptionInfo implementation)
- **Issue:** Plan's pseudo-code called `objVal.GetFieldValue(objVal.GetClass_(), targetRid, ...)` which is not valid C# (can't call a method in the middle of an argument list like that). Also the plan used `GetClass_()` but actual COM method name is `GetClass()`. TryReadStringField walks the inheritance chain, so the class needs to be obtained at the right inheritance level, not just the concrete type's class.
- **Fix:** TryReadStringField calls `objVal.GetClass(out ICorDebugClass cls)` inside the method body, then uses `cls` in the `GetFieldValue` call. This gets the concrete class each time, which works for GetFieldValue (ICorDebug resolves field lookup by token regardless of which class you pass).
- **Files modified:** src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs
- **Verification:** Build succeeds with no errors. Pattern matches how VariableReader uses GetClass + GetFieldValue.
- **Committed in:** 6b95046 (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (1 bug)
**Impact on plan:** Minor implementation correction for COM method naming and C# syntax. No scope creep.

## Issues Encountered

None - plan executed cleanly after correcting the COM method naming issue.

## Next Phase Readiness

- Exception events now carry real type names and messages — ready for Phase 7 plan 02 (threading) and plan 03 (attach)
- first-chance exception support tested at the source level; live testing via MCP will confirm runtime behavior

---
*Phase: 07-exceptions-threading-attach*
*Completed: 2026-02-23*
