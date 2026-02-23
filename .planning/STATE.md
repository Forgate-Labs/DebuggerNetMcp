# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-23)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Milestone v1.1 — Phase 6: Closures, Iterators & Object Graph

## Current Position

Phase: 5 of 9 (Type System) — COMPLETE
Plan: 3 of 3 complete
Status: Phase Complete — ready for Phase 6
Last activity: 2026-02-23 — 05-03 type system verification complete (all TYPE-01..04 verified)

Progress: [█████░░░░░] 55% (5/9 phases complete)

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
- Async hoisted vars: `<counter>5__2` → display as `counter`; `<>1__state` → skip
- Reverse PDB lookup not yet implemented — stacktrace shows hex tokens
- Static fields: ICorDebugClass.GetStaticFieldValue needs active thread — sequencing needed
- [Phase 05]: Pass nullable ICorDebugFrame? to GetStaticFieldValue — allows non-thread-static fields without frame context
- [Phase 05]: EvaluateAsync dot-notation lookup is highest priority — runs before state-machine and IL-local paths
- [Phase 05-01]: IsEnumType checks BaseType — TypeReference path (cross-assembly) AND TypeDefinitionHandle path (BCL same-assembly); both required for full enum coverage
- [Phase 05-01]: Nullable`1 detection works for CoreLib via GetTypeName — PE reads work for all modules
- [Phase 05-03]: Sections 13-16 inserted after section 9 (file had only 1-9); IsEnumType BCL fix adds TypeDefinitionHandle path for DayOfWeek and other System enums

### Blockers/Concerns

- Computed properties: no backing field in PE — may require IL eval or mark as "<computed>"
- Circular references: VariableReader.ReadObject has no depth tracking — stack overflow risk
- dotnet test: process model differs from launch — CreateProcess timing may vary

## Session Continuity

Last session: 2026-02-23
Stopped at: Completed 05-03-PLAN.md (HelloDebug type verification — all TYPE-01..04 confirmed; Phase 5 complete)
Resume file: None
