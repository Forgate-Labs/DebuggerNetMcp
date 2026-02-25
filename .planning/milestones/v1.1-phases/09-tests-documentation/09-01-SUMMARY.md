---
phase: 09-tests-documentation
plan: 01
subsystem: testing
tags: [xunit, pdb, unit-tests, InternalsVisibleTo]

# Dependency graph
requires:
  - phase: 08-stack-trace-and-dotnet-test
    provides: PdbReader.FindLocation + PdbReader.ReverseLookup static methods
provides:
  - xUnit test infrastructure with DebuggerFixture + DisableParallelization collection
  - PdbReaderTests: 5 unit tests for FindLocation (forward) and ReverseLookup (reverse)
  - MathTests: 2 unit tests for basic math (renamed from UnitTest1.cs)
  - HelloDebug auto-built via ProjectReference with ReferenceOutputAssembly=false
affects: [09-02-integration-tests, future test plans]

# Tech tracking
tech-stack:
  added: []
  patterns: [InternalsVisibleTo via csproj AssemblyAttribute, IAsyncLifetime fixture, CollectionDefinition with DisableParallelization]

key-files:
  created:
    - tests/DebuggerNetMcp.Tests/PdbReaderTests.cs
    - tests/DebuggerNetMcp.Tests/DebuggerFixture.cs
    - tests/DebuggerNetMcp.Tests/MathTests.cs
  modified:
    - tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj
    - src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj

key-decisions:
  - "InternalsVisibleTo added to Core csproj so PdbReader (internal static) is accessible from test assembly"
  - "DebuggerNetMcp.Core.Models global using removed — Models are in DebuggerNetMcp.Core.Engine namespace"
  - "HelloDebugDll path: 4 levels up from AppContext.BaseDirectory to reach tests/HelloDebug/bin/Debug/net10.0/"

patterns-established:
  - "ProjectReference with ReferenceOutputAssembly=false: builds dependency without polluting test assembly references"
  - "IAsyncLifetime fixture: async setup/teardown for DotnetDebugger in integration tests"
  - "DebuggerCollection with DisableParallelization=true: prevents concurrent debugger sessions"

requirements-completed: [TEST-02]

# Metrics
duration: 2min
completed: 2026-02-23
---

# Phase 09 Plan 01: xUnit Infrastructure + PdbReader Unit Tests Summary

**xUnit test infrastructure with DebuggerFixture, InternalsVisibleTo for PdbReader, and 7 passing tests (5 PdbReader + 2 MathTests)**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-23T22:15:39Z
- **Completed:** 2026-02-23T22:17:15Z
- **Tasks:** 2
- **Files modified:** 5

## Accomplishments
- Set up test infrastructure: DebuggerFixture (IAsyncLifetime), DebuggerCollection (DisableParallelization=true)
- 5 PdbReaderTests passing: HelloDebugDll_Exists, FindLocation for lines 17 and 25, ReverseLookup round-trip, nearest-SP semantics
- MathTests (renamed from UnitTest1.cs) preserved with 2 existing tests
- HelloDebug auto-built via ProjectReference with ReferenceOutputAssembly=false
- InternalsVisibleTo enables test access to internal PdbReader class

## Task Commits

Each task was committed atomically:

1. **Task 1: Update csproj, rename UnitTest1→MathTests, add DebuggerFixture** - `58b9048` (chore)
2. **Task 2: Write PdbReaderTests.cs — forward and reverse lookup** - `773499e` (test)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `tests/DebuggerNetMcp.Tests/PdbReaderTests.cs` - 5 unit tests for PdbReader.FindLocation and ReverseLookup
- `tests/DebuggerNetMcp.Tests/DebuggerFixture.cs` - IAsyncLifetime fixture + DebuggerCollection definition
- `tests/DebuggerNetMcp.Tests/MathTests.cs` - renamed from UnitTest1.cs, content preserved
- `tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj` - HelloDebug ProjectReference, global using Engine
- `src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj` - InternalsVisibleTo(DebuggerNetMcp.Tests)

## Decisions Made
- Added `InternalsVisibleTo` to Core csproj (via AssemblyAttribute) so `internal static class PdbReader` is accessible from the test assembly without changing PdbReader's visibility
- Removed `DebuggerNetMcp.Core.Models` global using — no such namespace exists; models live in `DebuggerNetMcp.Core.Engine`
- HelloDebugDll path uses 4 `..` segments from AppContext.BaseDirectory: `tests/DebuggerNetMcp.Tests/bin/Debug/net10.0/` → `tests/HelloDebug/bin/Debug/net10.0/`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added InternalsVisibleTo to Core csproj**
- **Found during:** Task 1 (reviewing PdbReader.cs before writing tests)
- **Issue:** PdbReader is `internal static` — test assembly cannot reference it without explicit visibility grant
- **Fix:** Added `<AssemblyAttribute Include="System.Runtime.CompilerServices.InternalsVisibleTo">` to Core csproj
- **Files modified:** src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj
- **Verification:** Build passes, PdbReader accessible from test assembly
- **Committed in:** 58b9048 (Task 1 commit)

**2. [Rule 1 - Bug] Removed non-existent DebuggerNetMcp.Core.Models global using**
- **Found during:** Task 1 (checking namespaces in Core project)
- **Issue:** Plan specified `<Using Include="DebuggerNetMcp.Core.Models" />` but that namespace doesn't exist (models are in DebuggerNetMcp.Core.Engine)
- **Fix:** Removed the invalid global using from Tests csproj
- **Files modified:** tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj
- **Verification:** Build succeeds without errors
- **Committed in:** 58b9048 (Task 1 commit)

---

**Total deviations:** 2 auto-fixed (1 missing critical, 1 bug)
**Impact on plan:** Both fixes were necessary to compile. No scope creep.

## Issues Encountered
None beyond the two auto-fixed deviations above.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Test infrastructure complete — Plan 02 can add integration tests using `[Collection("Debugger")]` with `DebuggerFixture`
- All 7 tests green, clean baseline established
- HelloDebug always rebuilt on `dotnet test` via ProjectReference

---
*Phase: 09-tests-documentation*
*Completed: 2026-02-23*
