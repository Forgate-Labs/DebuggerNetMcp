# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-22)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Phase 3 - Debug Engine

## Current Position

Phase: 3 of 5 (Debug Engine)
Plan: 5 of 5 in current phase — COMPLETE
Status: Phase Complete
Last activity: 2026-02-22 — Completed 03-05 (DotnetDebugger.cs inspection methods: GetStackTraceAsync, GetLocalsAsync, EvaluateAsync; VariableReader.ReadObjectFields; PdbReader.GetLocalNames)

Progress: [████████░░] ~75%

## Performance Metrics

**Velocity:**
- Total plans completed: 4
- Average duration: ~3 min/plan
- Total execution time: ~20 min

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 01-foundation | 3 | ~18min | ~6min |
| 02-interop-engine-foundation | 3 | ~8min | ~2.7min |

**Recent Trend:**
- Last 5 plans: 01-02 (CMake native), 01-03 (build.sh + install.sh), 02-01 (debug model types), 02-02 (COM interop), 02-03 (PdbReader + VariableReader)
- Trend: On track

*Updated after each plan completion*
| Phase 02-interop-engine-foundation P03 | 3min | 2 tasks | 2 files |
| Phase 03-debug-engine P01 | 1min | 1 task | 2 files |
| Phase 03-debug-engine P02 | 2min | 1 tasks | 1 files |
| Phase 03-debug-engine P03 | 3min | 1 tasks | 1 files |
| Phase 03-debug-engine P04 | 2min | 2 tasks | 2 files |
| Phase 03 P05 | 2min | 2 tasks | 4 files |

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
- [Phase 02-interop-engine-foundation]: CorElementType defined in VariableReader.cs (not ICorDebug.cs) — metadata concept, not COM interface concern
- [Phase 02-interop-engine-foundation]: Object field enumeration deferred to Phase 3 — requires ICorDebugModule.GetMetaDataInterface with running process
- [Phase 02-interop-engine-foundation]: FindAllLocations returns empty list (not throw) on FileNotFoundException — async methods map one source line to multiple SPs
- [Phase 03-debug-engine]: ICorDebugClass.GetModule added before GetToken to match cordebug.idl vtable order
- [Phase 03-debug-engine]: IMetaDataImportMinimal uses [ComImport] not [GeneratedComInterface] — 62 vtable slots declared to preserve correct offsets for EnumFields (17) and GetFieldProps (54)
- [Phase 03-debug-engine]: BreakpointTokenToId uses methodDef uint key (not Marshal.GetIUnknownForObject) — GetIUnknownForObject is Windows-only and incompatible with [GeneratedComInterface] source-generated proxies on Linux
- [Phase 03-debug-engine]: StrategyBasedComWrappers.GetOrCreateObjectForComInstance instead of Marshal.GetObjectForIUnknown — cross-platform Linux fix, eliminates SYSLIB1099/CA1416
- [Phase 03-debug-engine]: ResolveBreakpoint as regular private stub (not partial method) — DotnetDebugger is not declared partial class
- [Phase 03-debug-engine]: PdbReader.FindLocation throws on miss (not null) — used try/catch instead of null check from plan template
- [Phase 03-debug-engine]: BreakpointTokenToId keyed by uint methodDef (not Marshal.GetIUnknownForObject nint) — COM proxy identity instability on Linux
- [Phase 03]: ICorDebugChain GeneratedComInterface stub requires full vtable through EnumerateFrames per cordebug.idl
- [Phase 03]: Marshal.GetObjectForIUnknown retained for IMetaDataImportMinimal (CA1416 suppressed) — ComImport interfaces cannot use StrategyBasedComWrappers
- [Phase 03]: GetStackTraceAsync source location deferred — PdbReader only has forward (source->IL) lookup; reverse IL->source lookup is future work

### Pending Todos

None.

### Blockers/Concerns

- Phase 3 complete. GetStackTraceAsync returns method tokens (0x0600xxxx) not human-readable names — reverse PDB (IL→source) lookup deferred to Phase 4/5.
- libdbgshim.so path: must be discovered dynamically; reference location is ~/.local/bin/ (CoreCLR 9.0.13)

## Session Continuity

Last session: 2026-02-22
Stopped at: Completed 03-05-PLAN.md (DotnetDebugger.cs inspection methods + VariableReader ReadObject + PdbReader.GetLocalNames)
Resume file: None
