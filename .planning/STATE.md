# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-23)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Milestone v1.1 — Phase 6: Closures, Iterators & Object Graph

## Current Position

Phase: 6 of 9 (Closures, Iterators & Object Graph) — IN PROGRESS
Plan: 1 of 3 complete
Status: In Progress — 06-01 complete, ready for 06-02
Last activity: 2026-02-23 — 06-01 closure detection + iterator Current/_state exposure

Progress: [█████░░░░░] 55% (5/9 phases complete, Phase 6 in progress)

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

### Blockers/Concerns

- Computed properties: no backing field in PE — may require IL eval or mark as "<computed>"
- Circular references: VariableReader.ReadObject circular ref detection now COMPLETE (Phase 06-01 fixed incomplete research changes)
- dotnet test: process model differs from launch — CreateProcess timing may vary

## Session Continuity

Last session: 2026-02-23
Stopped at: Completed 06-01-PLAN.md (closure detection + iterator Current/_state exposure; ready for 06-02 live test)
Resume file: None
