---
phase: 02-interop-engine-foundation
plan: 03
subsystem: engine
tags: [csharp, pdb-reader, variable-reader, system-reflection-metadata, icordebug, portable-pdb]

# Dependency graph
requires:
  - 02-01 (VariableInfo record from Models.cs)
  - 02-02 (ICorDebugValue, ICorDebugGenericValue, ICorDebugStringValue, ICorDebugObjectValue, ICorDebugArrayValue, ICorDebugReferenceValue from ICorDebug.cs)
provides:
  - PdbReader.FindLocation(dllPath, sourceFile, line) → (methodToken, ilOffset)
  - PdbReader.FindAllLocations(dllPath, sourceFile, line) → List<(methodToken, ilOffset)>
  - VariableReader.ReadValue(name, ICorDebugValue, depth) → VariableInfo
  - CorElementType enum (all types from corhdr.h End through Pinned)
affects:
  - Phase 3 DotnetDebugger.cs (calls PdbReader for breakpoint placement, VariableReader for locals inspection)

# Tech tracking
tech-stack:
  added:
    - System.Reflection.Metadata (in-box in .NET 10, no NuGet needed)
    - System.Reflection.PortableExecutable (PEReader)
  patterns:
    - "Embedded-PDB-first fallback: TryOpenAssociatedPortablePdb if no embedded entry"
    - "methodToken = 0x06000000 | MetadataTokens.GetRowNumber(methodDebugHandle) (1-based)"
    - "MatchesSourceFile: EndsWith suffix OR filename equality — handles full/relative/basename callers"
    - "ICorDebugReferenceValue.Dereference() before ICorDebugObjectValue cast — required anti-pattern guard"
    - "Depth limit: depth > 3 returns placeholder rather than recursing — prevents infinite loops on cyclic graphs"
    - "Per-handler try/catch in VariableReader — one bad variable doesn't crash entire locals enumeration"
    - "Marshal.AllocHGlobal + PtrToStringUni for ICorDebugStringValue.GetString (IntPtr API from Plan 02-02 SYSLIB1051 fix)"

key-files:
  created:
    - src/DebuggerNetMcp.Core/Engine/PdbReader.cs
    - src/DebuggerNetMcp.Core/Engine/VariableReader.cs

key-decisions:
  - "CorElementType defined in VariableReader.cs (not ICorDebug.cs) — it is a metadata concept, not a COM interface concern"
  - "FindAllLocations returns empty list on FileNotFoundException (not throw) — async methods have multiple SPs per source line"
  - "Object field enumeration deferred to Phase 3 — requires ICorDebugModule.GetMetaDataInterface which needs the running process"
  - "Marshal.AllocHGlobal for GetString buffer — avoids char[] which would conflict with GeneratedComInterface SYSLIB1051"

patterns-established:
  - "Pattern: CorElementType switch with per-case reader methods — easy to extend for new types in Phase 3"
  - "Pattern: MaxDepth constant (3) and MaxArrayElements constant (10) — tunable without scattering magic numbers"

requirements-completed: [ENGINE-02, ENGINE-03]

# Metrics
duration: ~3min
completed: 2026-02-22
---

# Phase 02 Plan 03: PdbReader + VariableReader Summary

**PDB-based source-to-IL mapping (FindLocation/FindAllLocations) and recursive ICorDebugValue inspection (ReadValue) covering all 12+ CorElementType cases with reference dereference, depth limiting, and per-handler error isolation.**

## Performance

- **Duration:** ~3 min
- **Started:** 2026-02-22T22:43:46Z
- **Completed:** 2026-02-22T22:47:00Z
- **Tasks:** 2
- **Files modified:** 2 created

## Accomplishments

- Created `src/DebuggerNetMcp.Core/Engine/PdbReader.cs` with embedded-PDB-first strategy, methodToken computation `0x06000000 | rowNumber`, and flexible `MatchesSourceFile` helper
- Created `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` with full CorElementType enum, dispatch switch for 12+ element types, reference type dereference guard, depth limit, and per-handler error isolation
- Both files compile as part of `DebuggerNetMcp.Core` with 0 warnings and 0 errors
- Full solution build also passes (0 warnings, 0 errors)

## Task Commits

| Task | Commit | Description |
|------|--------|-------------|
| Task 1 | 7b153c1 | feat(02-03): add PdbReader.cs — source line to IL offset mapping |
| Task 2 | 61f2671 | feat(02-03): add VariableReader.cs — recursive ICorDebugValue inspection |

## Files Created/Modified

- `src/DebuggerNetMcp.Core/Engine/PdbReader.cs` — PdbReader class (144 lines): FindLocation, FindAllLocations, OpenPdbProvider, MatchesSourceFile
- `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` — VariableReader class + CorElementType enum (306 lines): ReadValue dispatch, 12 type-specific readers, ReadArray, ReadObject

## Decisions Made

- `CorElementType` defined in `VariableReader.cs` — it belongs to the metadata/value inspection layer, not the COM interface layer. Avoids polluting `ICorDebug.cs` with a non-COM concern.
- `FindAllLocations` returns empty list (not throw) on `FileNotFoundException` — callers that enumerate all locations for async methods should handle missing PDB gracefully.
- Object/struct field enumeration is explicitly deferred to Phase 3 with a comment — requires a running `ICorDebugModule` + metadata reader which is only available during an active debug session.
- Used `Marshal.AllocHGlobal` + `PtrToStringUni` for the `GetString` call — the `IntPtr`-based API in `ICorDebug.cs` (from Plan 02-02's SYSLIB1051 fix) requires native buffer allocation.

## Deviations from Plan

### Auto-fixed Issues

None observed — plan executed exactly as written.

The plan noted the `IntPtr`-based `GetString` API from Plan 02-02 and mentioned reading the string via `char[]`; the actual implementation used `Marshal.AllocHGlobal + PtrToStringUni` which is the correct approach for an `IntPtr` parameter (Rule 2 correctness requirement, inline fix during implementation).

## Issues Encountered

None.

## User Setup Required

None — no external service configuration required.

## Next Phase Readiness

- `PdbReader.FindLocation` is ready for Phase 3 `DotnetDebugger.SetBreakpointAsync` — returns `(methodToken, ilOffset)` consumed by `ICorDebugFunction.CreateBreakpoint`
- `VariableReader.ReadValue` is ready for Phase 3 locals/arguments enumeration via `ICorDebugILFrame.GetLocalVariable`
- Object field enumeration is the only deferred item — Phase 3 will add it via `ICorDebugModule.GetMetaDataInterface`
- No blockers for Phase 3

## Self-Check: PASSED

- [x] `src/DebuggerNetMcp.Core/Engine/PdbReader.cs` exists
- [x] `src/DebuggerNetMcp.Core/Engine/VariableReader.cs` exists
- [x] `.planning/phases/02-interop-engine-foundation/02-03-SUMMARY.md` exists
- [x] commit 7b153c1 exists in git log
- [x] commit 61f2671 exists in git log
- [x] `dotnet build` exits 0 with 0 warnings, 0 errors (Core + full solution)
- [x] `grep "0x06000000"` matches in PdbReader.cs
- [x] `grep "Dereference"` matches in VariableReader.cs
- [x] `grep "depth > MaxDepth"` confirms depth limit guard

---
*Phase: 02-interop-engine-foundation*
*Completed: 2026-02-22*
