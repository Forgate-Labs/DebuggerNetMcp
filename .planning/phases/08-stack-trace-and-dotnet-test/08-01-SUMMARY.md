---
phase: 08-stack-trace-and-dotnet-test
plan: 01
subsystem: debugging
tags: [pdb, portable-pdb, sequence-points, stack-trace, il-offset, source-mapping]

# Dependency graph
requires:
  - phase: 07-exceptions-threading-attach
    provides: GetStackFramesForThread helper + StackFrameInfo model
  - phase: 06-closures-iterators-object-graph
    provides: VariableReader.GetModulePath + PdbReader.GetMethodTypeFields
provides:
  - PdbReader.ReverseLookup(dllPath, methodToken, ilOffset) -> (sourceFile, line)?
  - GetStackFramesForThread populates StackFrameInfo.File (basename) and StackFrameInfo.Line from PDB
  - Real method names in stack frames (e.g. "MoveNext", "Main") instead of hex tokens
  - Graceful fallback to hex token for BCL/framework frames without PDB
affects: [debug_stacktrace output, phase 09]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "PDB reverse lookup: nearest sequence point with Offset <= ilOffset (ascending order per Portable PDB spec)"
    - "Non-fatal PDB resolution: try/catch wraps all PDB lookups in GetStackFramesForThread — BCL frames degrade gracefully"

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/PdbReader.cs
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Mcp/DebuggerTools.cs

key-decisions:
  - "ReverseLookup uses last sequence point with Offset <= ilOffset (not exact match) — nearest-sequence-point semantics match debugger conventions"
  - "break on Offset > ilOffset optimization is safe per Portable PDB spec ascending-offset guarantee"
  - "sourceFile stored in StackFrameInfo as Path.GetFileName(fullPath) — basename only for display; full path from PDB is discarded"
  - "ServerVersion bumped 0.7.9 -> 0.8.0 (minor bump: new user-visible feature — human-readable stack frames)"

patterns-established:
  - "PDB reverse lookup: use SequencePoint? best = null pattern scanning ascending sequence points, break when Offset exceeds ip"
  - "Stack frame resolution: always wrap in try/catch — framework frames (CoreLib, ASP.NET) have no PDB and must not crash"

requirements-completed: [STKT-01, STKT-02]

# Metrics
duration: 2min
completed: 2026-02-23
---

# Phase 08 Plan 01: Stack Trace Source Locations Summary

**PdbReader.ReverseLookup maps IL offsets to "Program.cs:57" style locations in debug_stacktrace, replacing unreadable hex method tokens**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-02-23T21:51:31Z
- **Completed:** 2026-02-23T21:52:52Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments
- Added `PdbReader.ReverseLookup(dllPath, methodToken, ilOffset)` — nearest-sequence-point reverse PDB lookup returning `(sourceFile, line)?`
- Wired ReverseLookup into `GetStackFramesForThread` — each IL frame now resolves source file (basename) and line number from PDB
- Method names now resolve from PE metadata via `GetMethodTypeFields` instead of showing hex tokens (e.g. "MoveNext" not "0x06000003")
- BCL/framework frames (no PDB) silently fall back to hex token — no crash, no noise
- Bumped ServerVersion 0.7.9 -> 0.8.0

## Task Commits

Each task was committed atomically:

1. **Task 1: Add PdbReader.ReverseLookup** - `6d495fe` (feat)
2. **Task 2: Wire ReverseLookup into GetStackFramesForThread + resolve method name** - `0e40a06` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `src/DebuggerNetMcp.Core/Engine/PdbReader.cs` - Added ReverseLookup static method (42 lines)
- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - Updated GetStackFramesForThread to call PdbReader.ReverseLookup and GetMethodTypeFields
- `src/DebuggerNetMcp.Mcp/DebuggerTools.cs` - Bumped ServerVersion 0.7.9 -> 0.8.0

## Decisions Made
- ReverseLookup uses nearest-sequence-point semantics (last SP with Offset <= ilOffset) — standard debugger convention, matches what Visual Studio does
- The `break` optimization when Offset > ilOffset is valid because Portable PDB spec guarantees ascending sequence point order
- Display uses `Path.GetFileName()` for source file — keeps output compact ("Program.cs:57" vs full absolute path)
- Minor version bump (0.7.9 -> 0.8.0) because this adds a user-visible feature (readable stack frames)

## Deviations from Plan

None - plan executed exactly as written.

## Issues Encountered
None.

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- Stack trace now shows human-readable source locations for user project frames
- debug_stacktrace output: frames will show `{ method: "MoveNext", file: "Program.cs", line: 57 }` for frames with PDB
- Ready for Phase 08 Plan 02 (dotnet test integration)

## Self-Check: PASSED

- FOUND: src/DebuggerNetMcp.Core/Engine/PdbReader.cs (ReverseLookup method added)
- FOUND: src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs (GetStackFramesForThread updated)
- FOUND: .planning/phases/08-stack-trace-and-dotnet-test/08-01-SUMMARY.md
- FOUND: commits 6d495fe, 0e40a06

---
*Phase: 08-stack-trace-and-dotnet-test*
*Completed: 2026-02-23*
