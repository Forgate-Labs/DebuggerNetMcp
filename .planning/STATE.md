# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-22)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Phase 1 - Foundation

## Current Position

Phase: 1 of 5 (Foundation)
Plan: 2 of TBD in current phase
Status: Executing
Last activity: 2026-02-22 — Completed 01-02 (CMake native ptrace wrapper)

Progress: [██░░░░░░░░] ~20%

## Performance Metrics

**Velocity:**
- Total plans completed: 2
- Average duration: ~8 min/plan
- Total execution time: ~16 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 2 | ~16min | ~8min |

**Recent Trend:**
- Last 5 plans: 01-01 (research), 01-02 (CMake native)
- Trend: On track

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- [Pre-Phase 1]: ICorDebug direct (no DAP) — eliminates netcoredbg race condition at the root
- [Pre-Phase 1]: PTRACE_SEIZE (not PTRACE_ATTACH) — kernel 6.12+ compatible
- [Pre-Phase 1]: Channel<DebugEvent> for async — decouples ICorDebug thread from MCP tools
- [Pre-Phase 1]: Dedicated thread for ICorDebug — COM requirement, all access on same thread
- [01-01]: SDK pinned to 10.0.0 with rollForward latestMinor — allows patch updates, blocks major/minor
- [01-01]: Core is classlib, Mcp is thin console entry point — clean separation of logic from wiring
- [01-01]: Tests references Core only (not Mcp) — unit tests target library logic directly
- [01-02]: LIBRARY_OUTPUT_DIRECTORY set to lib/ in CMakeLists.txt — avoids copy step in build.sh
- [01-02]: #include <stddef.h> required for NULL in GCC 13 strict C mode — added to ptrace_wrapper.c

### Pending Todos

None.

### Blockers/Concerns

- Phase 3 (Debug Engine) is the highest-risk phase: ICorDebug COM interop on Linux is underdocumented; dedicated thread + Channel pattern needs careful implementation
- libdbgshim.so path: must be discovered dynamically; reference location is ~/.local/bin/ (CoreCLR 9.0.13)

## Session Continuity

Last session: 2026-02-22
Stopped at: Completed 01-01-PLAN.md (C# solution scaffold — Python removal + .NET 10 setup)
Resume file: None
