# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-23)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Milestone v1.1 — Phase 5: Type System

## Current Position

Phase: 5 of 9 (Type System)
Plan: 2 of 3 complete
Status: In Progress
Last activity: 2026-02-23 — 05-02 static field reading complete

Progress: [████░░░░░░] 44% (4/9 phases complete)

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

### Blockers/Concerns

- Computed properties: no backing field in PE — may require IL eval or mark as "<computed>"
- Circular references: VariableReader.ReadObject has no depth tracking — stack overflow risk
- dotnet test: process model differs from launch — CreateProcess timing may vary

## Session Continuity

Last session: 2026-02-23
Stopped at: Completed 05-02-PLAN.md (static field reading — ICorDebugClass.GetStaticFieldValue + VariableReader + EvaluateAsync dot-notation)
Resume file: None
