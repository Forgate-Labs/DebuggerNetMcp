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

## v1.1 — Complete .NET Debug Coverage (completed 2026-02-25)

**Goal:** Expandir o debug engine para cobrir todos os padrões .NET — structs, enums, nullable, closures, iterators, static fields, referências circulares, exceções não-capturadas, multi-threading, attach, reverse PDB lookup, e debug de testes xUnit — com testes de integração automatizados e README completo.

**Shipped:**
- Phase 5 (Type System): Leitura de structs, enums, Nullable<T> e static fields via PE metadata (ICorDebugClass.GetStaticFieldValue)
- Phase 6 (Closures, Iterators & Object Graph): Variáveis capturadas em lambdas com nomes limpos, estado de iterators, detecção de referências circulares, computed properties
- Phase 7 (Exceptions, Threading & Attach): ExceptionEvent com tipo+mensagem real, first-chance notifications, inspeção multi-thread, debug_pause, debug_attach por PID
- Phase 8 (Stack Trace & dotnet test): PdbReader.ReverseLookup (IL→source:line), debug_launch_test para projetos xUnit via `dotnet test`
- Phase 9 (Tests & Documentation): 14 testes de integração xUnit automatizados, README reescrito com diagrama ASCII
- Phase 10 (Tech Debt): Scripts portáteis (sem paths hardcoded), CMake removido, testes THRD-03 e DTEST-02 adicionados

**Validated capabilities:**
- Sistema de tipos completo: struct, enum, Nullable<T>, static fields, computed properties
- Object graph: closures/iterators legíveis, detecção de referências circulares
- Exceções: second-chance com tipo+mensagem, first-chance configurável
- Multi-threading: enumerate threads, stack por thread, pause multithreaded
- Process Attach: debug_attach a processos .NET em execução por PID
- Stack frames legíveis: "Program.cs:57" via reverse PDB lookup
- dotnet test: debug dentro de xUnit test processes com breakpoints em [Fact]
- 14 testes de integração automatizados passando

**Stats:** 6 fases (5-10), 17 planos, 41 commits feat, 98 arquivos modificados, ~4.776 LOC C#, 3 dias

**Archive:** .planning/milestones/v1.1-ROADMAP.md, .planning/milestones/v1.1-REQUIREMENTS.md

---
