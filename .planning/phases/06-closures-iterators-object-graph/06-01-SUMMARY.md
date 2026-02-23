---
phase: 06-closures-iterators-object-graph
plan: 01
subsystem: debug-engine
tags: [icordebug, state-machine, closure, iterator, display-class, movenext]

# Dependency graph
requires:
  - phase: 05-type-system
    provides: GetLocalsAsync with async state machine (MoveNext) field reading
provides:
  - GetLocalsAsync with closure (>b__) detection — reads captured variables from <>c__DisplayClass fields
  - Iterator current value exposure — <>2__current shown as "Current", <>1__state shown as "_state"
  - VariableReader circular reference detection complete (ReadObjectFields visited param fixed)
affects:
  - 06-02 (closures + iterators live test)
  - any plan reading local variables from lambda or iterator frames

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Closure detection: smMethodName.Contains('>b__') triggers same this-reading path as MoveNext"
    - "Iterator exposure: specific <>2__current and <>1__state checks BEFORE the general <> skip guard"
    - "Visited set propagated through ReadObject → ReadObjectFields → ReadValue for circular ref safety"

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Engine/VariableReader.cs

key-decisions:
  - "Closure detection reuses exact same this-reading body as MoveNext — no duplication needed"
  - "<>2__current and <>1__state get specific checks before general <> skip to expose them as user-visible names"
  - "ReadObjectFields visited param added as nullable optional (HashSet<ulong>?) for backward compat"

patterns-established:
  - "State machine path trigger: (smMethodName == 'MoveNext' || isClosureMethod) — extensible OR condition"
  - "Field name priority: specific field checks first, then general prefix guards"

requirements-completed: [CLSR-01, CLSR-02]

# Metrics
duration: 15min
completed: 2026-02-23
---

# Phase 06 Plan 01: Closures and Iterators in GetLocalsAsync Summary

**GetLocalsAsync extended to show lambda captured variables and iterator Current/_state by reusing the existing MoveNext this-reading path with two targeted condition changes**

## Performance

- **Duration:** 15 min
- **Started:** 2026-02-23T13:22:33Z
- **Completed:** 2026-02-23T13:37:00Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments
- Lambda/closure frames (methods named `<<Main>$>b__0` on `<>c__DisplayClass`) now enter the `this`-reading path and show captured variables with clean names
- Iterator `<>2__current` field exposed as `"Current"` (last yielded value) instead of being skipped
- Iterator `<>1__state` field exposed as `"_state"` (position counter) instead of being skipped
- Circular reference detection in `VariableReader` completed (research-phase changes were incomplete — fixed as blocking deviation)

## Task Commits

Each task was committed atomically:

1. **Task 1: Closure detection + VariableReader fix** - `e8d7fd0` (feat)
2. **Task 2: Iterator Current/_state exposure** - `96d49e5` (feat)

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - GetLocalsAsync closure detection and iterator field exposure
- `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` - ReadObjectFields visited param fix (blocking deviation)

## Decisions Made
- Closure detection reuses the exact same `this`-reading loop body as `MoveNext` — no duplication. The field-name logic already handles plain names (closures) correctly via the `else` branch (`displayName = fieldName`).
- `<>2__current` and `<>1__state` are exposed with friendly names rather than skipped, as they carry meaningful user-visible state during iterator debugging.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Fixed incomplete research-phase changes in VariableReader.cs**
- **Found during:** Task 1 (build verification after closure detection change)
- **Issue:** Research phase had added circular reference detection to `VariableReader` — modified `ReadObject` to call `ReadObjectFields(name, typeName, objVal, depth, visited)` with a `visited` parameter, but never updated `ReadObjectFields`'s signature to accept it. This caused 6 CS1501 build errors.
- **Fix:** Added `HashSet<ulong>? visited = null` parameter to `ReadObjectFields`; added `visited ??= new HashSet<ulong>();` initialization; passed `visited` through to `ReadValue` recursive call inside the field loop.
- **Files modified:** `src/DebuggerNetMcp.Core/Engine/VariableReader.cs`
- **Verification:** `dotnet build` — Build succeeded, 0 errors
- **Committed in:** `e8d7fd0` (Task 1 commit)

---

**Total deviations:** 1 auto-fixed (blocking)
**Impact on plan:** Fix was required to compile. Completes the circular reference detection that was partially implemented in research phase. No scope creep.

## Issues Encountered
None beyond the blocking deviation above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Closure and iterator variable reading is code-complete
- Ready for live test in Phase 06-02: add HelloDebug sections for closures and iterators, verify variables appear with expected names at breakpoints
- Potential concern: `PdbReader.GetMethodTypeFields` must return fields for `<>c__DisplayClass` types — if it only returns fields for compiler-generated types with `MoveNext`, closures may not get fields. This will be validated in 06-02.

---
*Phase: 06-closures-iterators-object-graph*
*Completed: 2026-02-23*
