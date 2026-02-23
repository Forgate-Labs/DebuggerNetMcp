# DebuggerNetMcp (Reescrita C#)

## Current Milestone: v1.1 — Complete .NET Debug Coverage

**Goal:** Expandir o debug engine para cobrir todos os padrões .NET — structs, enums, nullable, closures, iterators, static fields, referências circulares, exceções não-capturadas, multi-threading, attach, e reverse PDB lookup — com testes xUnit e documentação completa.

**Target features:**
- Sistema de tipos: struct, enum, Nullable\<T\>, static fields, computed properties
- Closures e iterators: lambdas com variáveis capturadas, yield return state machines
- Grafo de objetos: detecção de referências circulares, profundidade configurável
- Exceções: não-capturadas (second-chance) + first-chance notifications
- Multi-threading: enumerate threads, stack por thread, pause multithreaded
- Process Attach: debug_attach em processo já em execução por PID
- Stack Trace: reverse PDB lookup (IL → arquivo:linha legível)
- dotnet test: debug dentro de xUnit test processes
- Testes xUnit: integration tests cobrindo todos os cenários
- Documentação: README reescrito com nova arquitetura

## What This Is

MCP server para debug interativo de aplicações .NET Core no Linux, expondo 14 ferramentas de debug para o Claude Code via stdio. O engine foi reescrito em C#/.NET 10 usando ICorDebug e libdbgshim.so diretamente — sem netcoredbg, sem Python — e funciona de forma confiável no kernel Linux 6.12+.

## Core Value

O debug deve funcionar de forma confiável no kernel Linux 6.12+ sem workarounds frágeis — o Claude Code consegue lançar, pausar, inspecionar variáveis e navegar por código .NET com uma única ferramenta.

## Requirements

### Validated

- ✓ MCP server C#/.NET 10 com 14 debug tools via stdio — v1.0 (Phase 1-4)
- ✓ Debug engine ICorDebug direto (sem netcoredbg) — v1.0
- ✓ Wrapper ptrace PTRACE_SEIZE, compatível kernel 6.12+ — v1.0
- ✓ Leitura de variáveis: primitivos, strings, arrays, List, Dictionary, records, objetos, async state machine — v1.0
- ✓ Breakpoints com offset exato via ICorDebugCode.CreateBreakpoint — v1.0
- ✓ Step-over/into/out com StepRange via PDB — v1.0
- ✓ Multiple BPs em mesmo método (chave composta methodToken+ilOffset) — v1.0
- ✓ Event channel + estado resetado corretamente entre sessões — v1.0

### Active

- [ ] Sistema de tipos: struct, enum, Nullable\<T\>, static fields, computed properties sem backing field
- [ ] Closures: variáveis capturadas em lambda (compiler-generated Display classes)
- [ ] Iterators: yield return state machine — valor current e campos internos
- [ ] Grafo de objetos: detecção de referências circulares com depth limit configurável
- [ ] Exceções não-capturadas: second-chance events + first-chance notifications
- [ ] Multi-threading: enumerate threads, stack por thread, pause multithreaded
- [ ] Process Attach: debug_attach por PID em processo .NET já em execução
- [ ] Reverse PDB lookup: IL offset → arquivo:linha para debug_stacktrace legível
- [ ] dotnet test debug: debug dentro de xUnit test processes
- [ ] xUnit integration tests cobrindo todos os cenários (basic + advanced)
- [ ] README reescrito com arquitetura, pré-requisitos, exemplos de todas as tools

### Out of Scope

- netcoredbg — eliminado completamente; é a razão desta reescrita
- Python — eliminado completamente; tudo em C# e C
- PTRACE_ATTACH — usar apenas PTRACE_SEIZE (compatibilidade kernel 6.12+)
- Paths hardcoded para libdbgshim.so — sempre descoberta dinâmica em runtime
- DAP (Debug Adapter Protocol) — a nova arquitetura usa ICorDebug diretamente, sem intermediário DAP

## Context

**Problema raiz:** netcoredbg crasha com SIGSEGV no kernel Linux 6.12+ por race condition em `ManagedDebuggerHelpers::Startup(IUnknown*)`. O callback do libdbgshim.so é disparado quando o CLR inicia, mas a thread criada via `clone3`/`pthread_create` conflita com o kernel 6.12+. O workaround com `strace -f` funciona mas é frágil e não é uma solução definitiva.

**Nova arquitetura:**
```
Claude Code → MCP Server (C#/stdio) → Debug Engine (C#) → dbgshim + ICorDebug → .NET Process
```

**Componentes:**
- `DebuggerNetMcp.Native` — biblioteca C com ptrace wrapper (PTRACE_SEIZE)
- `DebuggerNetMcp.Core` — engine de debug em C# (ICorDebug, PdbReader, VariableReader)
- `DebuggerNetMcp.Mcp` — console app C# expondo tools via MCP SDK oficial
- `DebuggerNetMcp.Tests` — xUnit com testes de integração

**Ambiente de referência:**
- Kernel: Linux 6.17.0-14-generic
- .NET: 10.0.0 (target), runtime 8.0.22 e 9.0.11 disponíveis
- libdbgshim.so: `~/.local/bin/` (CoreCLR 9.0.13)
- Projeto de teste: `/tmp/debug-test/HelloDebug/` (net9.0)

## Constraints

- **Tech Stack**: C# (.NET 10) + C (ptrace wrapper) — sem Python, sem netcoredbg
- **Kernel**: PTRACE_SEIZE obrigatório — compatibilidade com kernel 6.12+
- **Threading**: Todos os acessos ICorDebug na mesma thread que inicializou a sessão
- **libdbgshim**: Path resolvido em runtime via NativeLibrary.Load(), nunca hardcoded
- **MCP SDK**: NuGet `ModelContextProtocol` oficial

## Key Decisions

| Decision | Rationale | Outcome |
|----------|-----------|---------|
| ICorDebug direto (sem DAP) | Elimina camada intermediária netcoredbg, resolve race condition na raiz | ✓ Good |
| PTRACE_SEIZE vs PTRACE_ATTACH | PTRACE_SEIZE é compatível com kernel 6.12+, não causa SIGSEGV | ✓ Good |
| Channel<DebugEvent> para async | Desacopla thread do ICorDebug das tools async do MCP | ✓ Good |
| Thread dedicada para ICorDebug | Requisito do COM — todos os acessos devem ser na mesma thread | ✓ Good |
| C# + C (não Go ou Rust) | .NET 10 tem acesso nativo a COM interfaces ICorDebug, mesmo runtime alvo | ✓ Good |
| ICorDebugCode.CreateBreakpoint com ilOffset exato | fn.CreateBreakpoint() usava offset 0 — JIT do .NET 10 ignorava; fix: GetILCode().CreateBreakpoint(offset) | ✓ Good |
| BreakpointTokenToId chave composta (token,offset) | Multiple BPs em mesmo método (ex: async MoveNext) compartilhavam key → IDs errados | ✓ Good |
| StepRange com PDB ao invés de Step() | Step() não localiza PDB no .NET 10 → single instruction; StepRange com range do PDB resolve | ✓ Good |
| Event channel sempre recriado em LaunchAsync | Reader.Completion.IsCompleted falso quando canal tem itens buffered após TryComplete | ✓ Good |

---
*Last updated: 2026-02-23 after v1.1 milestone start*
