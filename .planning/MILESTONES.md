# Milestones: DebuggerNetMcp

## v1.0 — C# Rewrite (completed 2026-02-23)

**Goal:** Reescrever o MCP server do zero em C#/.NET 10 usando ICorDebug direto, eliminando netcoredbg e o SIGSEGV no kernel 6.12+.

**Shipped:**
- Phase 1 (Foundation): Solução C# com 4 projetos, wrapper ptrace nativo (PTRACE_SEIZE), build.sh e install.sh
- Phase 2 (Interop + Engine Foundation): COM interfaces ICorDebug completas, PdbReader, VariableReader, Models
- Phase 3 (Debug Engine): DotnetDebugger.cs completo — launch, attach, breakpoints, step, inspect, async channel
- Phase 4 (MCP Server): 14 tools via ModelContextProtocol SDK, HelloDebug com 12 seções de teste

**Validated capabilities:**
- launch → set_breakpoint → continue → variables → step → evaluate — fluxo completo funcionando
- Tipos: primitivos, strings, arrays, List, Dictionary, records, objetos aninhados, exceções capturadas
- Async state machine (variáveis hoistadas como campos MoveNext)
- Step-over, step-into, step-out com StepRange via PDB
- Multiple BPs em mesmo método (chave composta token+offset)
- Event channel sempre recriado entre sessões
- Reset de estado entre sessões (sem COM objects stale)
- HelloDebug seções 1-12 todas testadas e passando manualmente

**Deferred to v1.1:**
- Phase 5 (Tests + Docs): TEST-02, TEST-03 (xUnit integration tests), DOCS-01 (README rewrite)

**Last phase:** 4 of 5 (Phase 5 deferred)

---

## v1.1 — Complete .NET Debug Coverage (started 2026-02-23)

**Goal:** Expandir o debug engine para cobrir todos os padrões .NET — structs, enums, nullable, closures, iterators, static fields, referências circulares, exceções não-capturadas, multi-threading, attach, e reverse PDB lookup — com testes xUnit e documentação completa.

**Starting phase:** 5 (continues from v1.0)
