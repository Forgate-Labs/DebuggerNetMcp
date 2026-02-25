---
phase: 03-debug-engine
plan: 01
subsystem: interop
tags: [com-interop, icordebug, imetadataimport, vtable, marshalling, csharp]

# Dependency graph
requires:
  - phase: 02-interop-engine-foundation
    provides: ICorDebug COM stubs, ICorDebugClass stub (empty), ICorDebugModule.GetMetaDataInterface
provides:
  - ICorDebugClass.GetToken and GetModule methods for typedef token retrieval
  - IMetaDataImportMinimal interface with 62-method vtable for field enumeration
  - Foundation for ReadObject field enumeration in Phase 3 DotnetDebugger/VariableReader
affects: [03-debug-engine, DotnetDebugger, VariableReader]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "ComImport + InterfaceIsIUnknown for large COM interfaces with ~70 methods to preserve vtable correctness"
    - "GeneratedComInterface only for interfaces where all methods are declared (vtable must be complete)"

key-files:
  created:
    - src/DebuggerNetMcp.Core/Interop/IMetaDataImport.cs
  modified:
    - src/DebuggerNetMcp.Core/Interop/ICorDebug.cs

key-decisions:
  - "ICorDebugClass.GetModule added before GetToken to match real cordebug.idl vtable order"
  - "IMetaDataImportMinimal uses [ComImport] not [GeneratedComInterface] — 62-method vtable preserves correct slot offsets for EnumFields (17) and GetFieldProps (54)"
  - "All 62 IMetaDataImport vtable slots declared even though only 3 are called at runtime (CloseEnum, EnumFields, GetFieldProps)"

patterns-established:
  - "Large COM interface pattern: [ComImport] + [InterfaceIsIUnknown] with all vtable slots as placeholder stubs"

requirements-completed: [ENGINE-08]

# Metrics
duration: 1min
completed: 2026-02-22
---

# Phase 03 Plan 01: COM Interop Extensions Summary

**ICorDebugClass.GetToken stub and 62-slot IMetaDataImportMinimal ([ComImport]) interface enabling field enumeration via GetMetaDataInterface in Phase 3**

## Performance

- **Duration:** 1 min
- **Started:** 2026-02-22T23:40:19Z
- **Completed:** 2026-02-22T23:41:45Z
- **Tasks:** 1
- **Files modified:** 2

## Accomplishments
- Added `GetModule(out ICorDebugModule)` and `GetToken(out uint pTypeDef)` to `ICorDebugClass` stub
- Created `IMetaDataImportMinimal` with all 62 IMetaDataImport vtable slots using `[ComImport]` pattern
- Solution builds with 0 errors and 0 warnings after both changes

## Task Commits

Each task was committed atomically:

1. **Task 1: Add ICorDebugClass.GetToken and create IMetaDataImportMinimal** - `1e37b65` (feat)

**Plan metadata:** *(docs commit follows)*

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` - Added GetModule + GetToken to ICorDebugClass stub
- `src/DebuggerNetMcp.Core/Interop/IMetaDataImport.cs` - New file: IMetaDataImportMinimal with 62 vtable methods, [ComImport] pattern, real IMetaDataImport GUID

## Decisions Made
- **GetModule added before GetToken:** The real cordebug.idl vtable for ICorDebugClass has GetModule as slot 0 and GetToken as slot 1. Both were added to ensure correct vtable indexing in case the interface is queried and other methods are called.
- **[ComImport] over [GeneratedComInterface]:** IMetaDataImport has ~70 methods. Using `[GeneratedComInterface]` with a partial declaration would produce an incorrect vtable layout. `[ComImport]` + `[InterfaceIsIUnknown]` with all 62 slots declared preserves the correct vtable offsets needed for EnumFields (slot 17) and GetFieldProps (slot 54).
- **All 62 slots declared:** Even placeholder methods must be present in the correct order. Only CloseEnum (0), EnumFields (17), and GetFieldProps (54) are called at runtime, but all preceding slots must exist for COM vtable indexing to work correctly.

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Self-Check: PASSED

All artifacts verified:
- `src/DebuggerNetMcp.Core/Interop/IMetaDataImport.cs` exists
- `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` modified with GetToken
- Commit `1e37b65` exists in git log
- Build: 0 errors, 0 warnings

## Next Phase Readiness
- Phase 3 DotnetDebugger and VariableReader can now call `ICorDebugClass.GetToken` to retrieve the typedef token
- `IMetaDataImportMinimal` is ready to be used after `ICorDebugModule.GetMetaDataInterface` returns a metadata interface pointer
- Pattern established: for future large COM interfaces, use `[ComImport]` + full vtable slot coverage

---
*Phase: 03-debug-engine*
*Completed: 2026-02-22*
