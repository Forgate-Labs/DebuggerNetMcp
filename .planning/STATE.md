# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-23)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Milestone v1.1 — Phase 7: Exceptions, Threading & Attach

## Current Position

Phase: 7 of 9 (Exceptions, Threading & Attach) — IN PROGRESS
Plan: 3 of 3 (checkpoint — awaiting human verification)
Status: Phase 7 plan 03 tasks complete — AttachAsync TCS confirmation + HelloDebug sections 20-21; checkpoint awaits human sign-off
Last activity: 2026-02-23 — 07-03 attach confirmation: attachConfirmedTcs pattern + sections 20 (multi-thread) + 21 (unhandled exception)

Progress: [██████░░░░] 67% (6/9 phases complete; phase 7 in progress)

## Performance Metrics

**Velocity:**
- Total plans completed: 13
- Average duration: unknown
- Total execution time: unknown

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 1. Foundation | 3 | - | - |
| 2. Interop + Engine Foundation | 3 | - | - |
| 3. Debug Engine | 5 | - | - |
| 4. MCP Server | 2 | - | - |
| Phase 05 P02 | 179 | 2 tasks | 4 files |
| Phase 05-type-system P03 | 10 | 2 tasks | 1 files |
| Phase 06-closures-iterators-object-graph P01 | 15 | 2 tasks | 2 files |
| Phase 06-closures-iterators-object-graph P02 | 4 | 2 tasks | 1 file |
| Phase 07-exceptions-threading-attach P02 | 3 | 2 tasks | 2 files |
| Phase 07-exceptions-threading-attach P03 | 8 | 2 tasks | 3 files |

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.

**v1.0 validated:**
- ICorDebug direto (sem DAP) — eliminates netcoredbg race condition at root
- PTRACE_SEIZE — compatible with kernel 6.12+
- ICorDebugCode.CreateBreakpoint(ilOffset) — exact offset fix for .NET 10 JIT
- Event channel sempre recriado — buffered items cause false IsCompleted

**v1.1 known constraints:**
- IMetaDataImport NÃO funciona no Linux — usar PEReader para metadata
- Async hoisted vars: `<counter>5__2` → display as `counter`; `<>1__state` → exposed as `_state` (Phase 06-01 change)
- Reverse PDB lookup not yet implemented — stacktrace shows hex tokens
- Static fields: ICorDebugClass.GetStaticFieldValue needs active thread — sequencing needed
- [Phase 05]: Pass nullable ICorDebugFrame? to GetStaticFieldValue — allows non-thread-static fields without frame context
- [Phase 05]: EvaluateAsync dot-notation lookup is highest priority — runs before state-machine and IL-local paths
- [Phase 05-01]: IsEnumType checks BaseType — TypeReference path (cross-assembly) AND TypeDefinitionHandle path (BCL same-assembly); both required for full enum coverage
- [Phase 05-01]: Nullable`1 detection works for CoreLib via GetTypeName — PE reads work for all modules
- [Phase 05-03]: Sections 13-16 inserted after section 9 (file had only 1-9); IsEnumType BCL fix adds TypeDefinitionHandle path for DayOfWeek and other System enums
- [Phase 06-closures-iterators-object-graph]: Closure detection: smMethodName.Contains('>b__') triggers the same this-reading path as MoveNext — no code duplication needed
- [Phase 06-closures-iterators-object-graph]: Iterator fields <>2__current and <>1__state exposed with friendly names (Current, _state) instead of skipped — specific checks before general <> guard
- [Phase 06-02]: Circular reference guard: GetAddress() after Dereference(); addr==0 guard handles unaddressable values; failure is non-fatal
- [Phase 06-02]: Computed property backing-field check uses only concrete typedefToken (not inheritance chain) — backing fields always declared on same type as property
- [Phase 06-03]: SuppressExitProcess flag on old ManagedCallbackHandler prevents old session's ExitProcess from closing new session's event channel
- [Phase 06-03]: LaunchAsync calls DisconnectAsync at startup to clear stale module registry from prior session — ensures pending breakpoints are registered fresh
- [Phase 07-01]: v1 Exception callback owns unhandled stop + sets _exceptionStopPending; v2 defers to avoid double-reporting
- [Phase 07-01]: TryReadExceptionInfo reads real exception type/message via GetCurrentException + PE metadata + _message field walk
- [Phase 07-01]: GetTypeName, GetModulePath, ReadInstanceFieldsFromPE, GetBaseTypeToken changed to internal static in VariableReader (reused from ManagedCallbackHandler)
- [Phase 07-01]: First-chance exceptions opt-in via debug_launch firstChanceExceptions=true; default false to avoid noise
- [Phase 07-exceptions-threading-attach]: GetAllThreads uses celt=1 loop — same pattern as chain/frame enumeration to avoid LPArray COM marshaling issues
- [Phase 07-exceptions-threading-attach]: thread_id=0 sentinel enables all-threads path in debug_stacktrace, backward-compatible default
- [Phase 07-exceptions-threading-attach]: GetStackFramesForThread extracted as shared helper reused by GetStackTraceAsync and GetAllThreadStackTracesAsync
- [Phase 07-exceptions-threading-attach]: attachConfirmedTcs: set OnProcessCreated before dispatch to avoid race; handler sets _process + resolves TCS with confirmed PID

### Blockers/Concerns

- Computed properties: RESOLVED (Phase 06-02) — PE property scan adds <computed> entries
- Circular references: RESOLVED (Phase 06-01/06-02) — HashSet<ulong> visited guards against cyclic graphs
- dotnet test: process model differs from launch — CreateProcess timing may vary

## Session Continuity

Last session: 2026-02-23
Stopped at: Checkpoint 07-03 — tasks 1+2 committed (b1e9f93, 9a89c0f); awaiting human verification of all 6 Phase 7 requirements
Resume file: None
