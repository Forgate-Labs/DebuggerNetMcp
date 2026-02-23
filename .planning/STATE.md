# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-02-23)

**Core value:** Debug .NET works reliably on Linux kernel 6.12+ without fragile workarounds
**Current focus:** Milestone v1.1 — Complete .NET Debug Coverage

## Current Position

Phase: Not started (defining requirements)
Plan: —
Status: Defining requirements
Last activity: 2026-02-23 — Milestone v1.1 started

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.

**v1.0 validated decisions:**
- ICorDebug direto (sem DAP) ✓ — eliminates netcoredbg race condition at the root
- PTRACE_SEIZE ✓ — compatible with kernel 6.12+
- Channel<DebugEvent> ✓ — decouples ICorDebug thread from MCP async tools
- Thread dedicada para ICorDebug ✓ — COM requirement enforced
- ICorDebugCode.CreateBreakpoint(ilOffset) ✓ — exact offset fix for .NET 10 JIT
- BreakpointTokenToId chave (token,offset) ✓ — multiple BPs in same method
- StepRange com PDB ✓ — Step() one-instruction limitation in .NET 10
- Event channel sempre recriado ✓ — buffered items cause false IsCompleted

**v1.1 architectural notes (from current session):**
- IMetaDataImport NÃO funciona no Linux (COM Interop not supported) — usar PEReader
- Variáveis async hoistadas: nome `<counter>5__2` → exibir `counter`; `<>1__state` → skip
- MoveNext `this` argument = state machine instance (ICorDebugReferenceValue → Dereference() → ICorDebugObjectValue)
- GetFieldValue para reference types retorna ICorDebugReferenceValue → Dereference() antes de cast
- GetModulePathInternal via Marshal.AllocHGlobal + PtrToStringUni (GetName via unmanaged buffer)
- frame.CreateStepper mais preciso que thread.CreateStepper para async contexts

### Pending Todos

None.

### Blockers/Concerns

- Reverse PDB lookup (IL→source) não implementado ainda — debug_stacktrace retorna tokens (0x0600xxxx)
- Static fields: ICorDebugClass.GetStaticFieldValue requires active thread — sequencing needed
- Computed properties: sem backing field no PE — pode requerer IL eval ou reflection
- Circular references: VariableReader.ReadObject não tem depth tracking — pode stack overflow em grafos circulares

## Session Continuity

Last session: 2026-02-23
Stopped at: Completed v1.0 manual testing (all 12 HelloDebug sections passing), started v1.1 milestone
Resume file: None
