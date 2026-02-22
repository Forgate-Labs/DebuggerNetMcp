---
phase: 03-debug-engine
plan: 05
subsystem: debug-engine
tags: [cordebug, icordebug, imetadataimport, pdb, variable-inspection, stack-trace, linq-local-eval]

# Dependency graph
requires:
  - phase: 03-debug-engine plan 04
    provides: DotnetDebugger.cs execution control + breakpoint management
  - phase: 02-interop-engine-foundation plan 03
    provides: PdbReader + VariableReader foundations, IMetaDataImportMinimal interface
provides:
  - GetStackTraceAsync — walks ICorDebugChainEnum/ICorDebugFrameEnum, returns StackFrameInfo list
  - GetLocalsAsync — enumerates local variables by slot index with PDB name mapping
  - EvaluateAsync — local variable lookup by name, returns EvalResult
  - VariableReader.ReadObjectFields — real field enumeration via IMetaDataImportMinimal
  - PdbReader.GetLocalNames — slot→name mapping from MethodDebugInformation LocalScope
affects: [04-mcp-tools, 05-testing]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - ICorDebugChainEnum/ICorDebugFrameEnum traversal for stack snapshot
    - GetLocalVariable index loop with CORDBG_E_IL_VAR_NOT_AVAILABLE sentinel
    - IMetaDataImportMinimal.EnumFields + GetFieldProps for object field enumeration
    - Marshal.AllocHGlobal/PtrToStringUni for native UTF-16 buffer reads
    - PDB LocalScope.GetLocalVariables for slot→name mapping

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/VariableReader.cs
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Core/Engine/PdbReader.cs
    - src/DebuggerNetMcp.Core/Interop/ICorDebug.cs

key-decisions:
  - "ICorDebugChain.EnumerateFrames requires full vtable declaration in GeneratedComInterface — added GetThread/GetRanges/GetContext/GetCaller/GetCallee/GetPrevious/GetNext/IsManaged before EnumerateFrames to match cordebug.idl vtable order"
  - "GetLocalNames uses MetadataTokens.MethodDefinitionHandle + ToDebugInformationHandle for method lookup; silently returns empty dict on FileNotFoundException/BadImageFormatException"
  - "IMetaDataImportMinimal GetObjectForIUnknown path preserved with CA1416 suppression — [ComImport] interface requires legacy RCW path, StrategyBasedComWrappers is only for [GeneratedComInterface] types"
  - "GetStackTraceAsync source location deferred (TODO comment) — PdbReader only has FindLocation (source→IL); reverse IL→source lookup deferred to future work"
  - "ICorDebugFrameEnum added as new GeneratedComInterface stub with correct GUID CC7BCB09"

patterns-established:
  - "CORDBG_E_IL_VAR_NOT_AVAILABLE (0x80131304) as sentinel for end of local variable enumeration"
  - "Module name extracted with Marshal.AllocHGlobal/PtrToStringUni pattern (consistent with OnModuleLoaded)"
  - "Object field enumeration: GetClass → GetToken + GetModule → GetMetaDataInterface → EnumFields loop → GetFieldProps per token"

requirements-completed: [ENGINE-07]

# Metrics
duration: 2min
completed: 2026-02-22
---

# Phase 03 Plan 05: Inspection Methods Summary

**ICorDebug variable inspection complete: GetStackTraceAsync/GetLocalsAsync/EvaluateAsync wired to VariableReader via IMetaDataImportMinimal field enumeration and PDB slot-name mapping**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-22T00:15:46Z
- **Completed:** 2026-02-22T00:18:00Z
- **Tasks:** 2
- **Files modified:** 4

## Accomplishments
- ReadObject in VariableReader.cs now enumerates object fields via IMetaDataImportMinimal.EnumFields + GetFieldProps (replaces Phase 2 placeholder)
- GetStackTraceAsync traverses ICorDebugChainEnum → ICorDebugFrameEnum → IL offset per frame
- GetLocalsAsync enumerates locals by slot index with CORDBG_E_IL_VAR_NOT_AVAILABLE sentinel, names from PDB
- EvaluateAsync resolves variable names via PdbReader.GetLocalNames slot map, returns EvalResult
- PdbReader.GetLocalNames added: reads MethodDebugInformation.LocalScope.GetLocalVariables, returns slot→name dictionary

## Task Commits

Each task was committed atomically:

1. **Task 1: Complete ReadObject in VariableReader.cs using IMetaDataImportMinimal** - `4eee407` (feat)
2. **Task 2: Add GetStackTraceAsync, GetLocalsAsync, EvaluateAsync to DotnetDebugger.cs** - `91084aa` (feat)

**Plan metadata:** (docs commit — see below)

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` - ReadObjectFields + GetFieldName replacing placeholder stub
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - GetStackTraceAsync, GetLocalsAsync, EvaluateAsync added
- `src/DebuggerNetMcp.Core/Engine/PdbReader.cs` - GetLocalNames method added
- `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` - ICorDebugChain vtable expanded, ICorDebugChainEnum.Next added, ICorDebugFrameEnum added

## Decisions Made
- ICorDebugChain `[GeneratedComInterface]` stub required full vtable through `EnumerateFrames` per cordebug.idl — added 12 methods in correct order
- `Marshal.GetObjectForIUnknown` retained for `IMetaDataImportMinimal` (CA1416 suppressed) — `[ComImport]` interfaces cannot use `StrategyBasedComWrappers`; that pattern is only for `[GeneratedComInterface]` types
- Source location in GetStackTraceAsync deferred — PdbReader only has forward (source→IL) lookup; reverse lookup needs future work
- `ICorDebugFrameEnum` needed as new interface (was absent from ICorDebug.cs stubs) — added with GUID CC7BCB09 matching cordebug.idl

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 3 - Blocking] Added ICorDebugChain vtable methods and ICorDebugFrameEnum**
- **Found during:** Task 2 (GetStackTraceAsync implementation)
- **Issue:** `ICorDebugChain` was an empty stub; `EnumerateFrames` call would use wrong vtable slot. `ICorDebugFrameEnum` was not defined at all.
- **Fix:** Expanded ICorDebugChain with 12 methods per cordebug.idl vtable order; added ICorDebugFrameEnum with Next + Skip/Reset/Clone/GetCount; added ICorDebugChainEnum.Next.
- **Files modified:** `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs`
- **Verification:** Build passes 0 errors, 0 warnings
- **Committed in:** `91084aa` (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (blocking — missing vtable methods)
**Impact on plan:** Necessary for correct COM vtable dispatch. No scope creep.

## Issues Encountered
None — plan executed without unexpected errors. All COM interface expansion was anticipated by the plan's note about checking ICorDebug.cs signatures.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Phase 3 complete: DotnetDebugger supports full debug cycle: launch → breakpoint → inspect locals → step → inspect → continue → exit
- Phase 4 (MCP tools) can now wire GetStackTraceAsync, GetLocalsAsync, EvaluateAsync to tool handlers
- Known gap: GetStackTraceAsync returns method tokens (0x0600xxxx) instead of human-readable method names; reverse PDB lookup deferred

---
*Phase: 03-debug-engine*
*Completed: 2026-02-22*
