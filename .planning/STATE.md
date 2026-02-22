# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-22)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Phase 2 - Interop Engine Foundation

## Current Position

Phase: 2 of 5 (Interop Engine Foundation)
Plan: 2 of 4 in current phase
Status: Executing
Last activity: 2026-02-22 — Completed 02-02 (COM interop layer: DbgShimInterop + ICorDebug interfaces)

Progress: [████░░░░░░] ~35%

## Performance Metrics

**Velocity:**
- Total plans completed: 3
- Average duration: ~6 min/plan
- Total execution time: ~18 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3 | ~18min | ~6min |
| 02-interop-engine-foundation | 2 | ~5min | ~2.5min |

**Recent Trend:**
- Last 5 plans: 01-01 (research), 01-02 (CMake native), 01-03 (build.sh + install.sh), 02-01 (debug model types), 02-02 (COM interop)
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
- [Phase 01-foundation]: [01-03]: -- separator required before server name in claude mcp add (variadic -e flag)
- [Phase 01-foundation]: [01-03]: CLAUDE_BIN env var overridable — avoids hardcoded claude binary path in install.sh
- [02-01]: All model types are records (not classes) — immutability enforced by design, value equality built-in
- [02-01]: DebugEvent uses abstract record + sealed subclasses — exhaustive switch expressions in Phase 3 without catch-all arms
- [02-01]: VariableInfo.Children typed as IReadOnlyList<VariableInfo> — callers cannot mutate the list
- [Phase 02]: AllowUnsafeBlocks enabled in DebuggerNetMcp.Core.csproj — required by [GeneratedComInterface] source generator
- [Phase 02]: All ICorDebug stub interfaces use real GUIDs from cordebug.idl (not placeholders) — ensures vtable correctness if native code queries these interfaces

### Pending Todos

None.

### Blockers/Concerns

- Phase 3 (Debug Engine) is the highest-risk phase: ICorDebug COM interop on Linux is underdocumented; dedicated thread + Channel pattern needs careful implementation
- libdbgshim.so path: must be discovered dynamically; reference location is ~/.local/bin/ (CoreCLR 9.0.13)

## Session Continuity

Last session: 2026-02-22
Stopped at: Completed 02-02-PLAN.md (COM interop layer: DbgShimInterop + ICorDebug interfaces)
Resume file: None
