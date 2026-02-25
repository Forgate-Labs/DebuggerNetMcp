# DebuggerNetMcp

## What This Is

MCP server para debug interativo de aplicações .NET Core no Linux, expondo 15 ferramentas de debug para o Claude Code via stdio. O engine usa ICorDebug e libdbgshim.so diretamente em C#/.NET 10 — sem netcoredbg, sem Python — e funciona de forma confiável no kernel Linux 6.12+. Cobre o sistema de tipos .NET completo (structs, enums, nullable, closures, iterators, static fields), multi-threading, process attach, stack traces legíveis e debug de testes xUnit.

## Core Value

O debug deve funcionar de forma confiável no kernel Linux 6.12+ sem workarounds frágeis — o Claude Code consegue lançar, pausar, inspecionar variáveis e navegar por código .NET com uma única ferramenta.

## Requirements

### Validated

- ✓ MCP server C#/.NET 10 com 14 debug tools via stdio — v1.0 (Phases 1-4)
- ✓ Debug engine ICorDebug direto (sem netcoredbg) — v1.0
- ✓ Wrapper ptrace PTRACE_SEIZE, compatível kernel 6.12+ — v1.0
- ✓ Leitura de variáveis: primitivos, strings, arrays, List, Dictionary, records, objetos, async state machine — v1.0
- ✓ Breakpoints com offset exato via ICorDebugCode.CreateBreakpoint — v1.0
- ✓ Step-over/into/out com StepRange via PDB — v1.0
- ✓ Multiple BPs em mesmo método (chave composta methodToken+ilOffset) — v1.0
- ✓ Event channel + estado resetado corretamente entre sessões — v1.0
- ✓ Sistema de tipos: struct, enum, Nullable<T>, static fields, computed properties — v1.1 (Phase 5)
- ✓ Closures: variáveis capturadas em lambda com nomes limpos — v1.1 (Phase 6)
- ✓ Iterators: yield return state machine — Current e estado interno — v1.1 (Phase 6)
- ✓ Grafo de objetos: detecção de referências circulares com HashSet<ulong> — v1.1 (Phase 6)
- ✓ Exceções: second-chance events com tipo+mensagem + first-chance notifications — v1.1 (Phase 7)
- ✓ Multi-threading: enumerate threads, stack por thread, pause multithreaded — v1.1 (Phase 7)
- ✓ Process Attach: debug_attach por PID em processo .NET em execução — v1.1 (Phase 7)
- ✓ Reverse PDB lookup: IL offset → arquivo:linha legível em debug_stacktrace — v1.1 (Phase 8)
- ✓ dotnet test debug: debug_launch_test para xUnit test processes — v1.1 (Phase 8)
- ✓ 14 testes de integração xUnit automatizados (PdbReader + Debugger + Advanced) — v1.1 (Phase 9)
- ✓ README reescrito com arquitetura, pré-requisitos, exemplos de todas as tools — v1.1 (Phase 9)
- ✓ Scripts portáteis: install.sh + strace wrapper sem paths hardcoded — v1.1 (Phase 10)

### Active

*(Nenhum — todos os requisitos de v1.1 foram validados)*

### Out of Scope

- netcoredbg — eliminado completamente; é a razão desta reescrita
- Python — eliminado completamente; tudo em C# e C
- PTRACE_ATTACH — usar apenas PTRACE_SEIZE (compatibilidade kernel 6.12+)
- Paths hardcoded para libdbgshim.so — sempre descoberta dinâmica em runtime
- DAP (Debug Adapter Protocol) — a nova arquitetura usa ICorDebug diretamente, sem intermediário DAP
- Expression evaluation complexa (ICorDebug Eval) — v2.0+
- Suporte Windows — foco no problema Linux kernel 6.12+

## Context

**Estado atual (pós v1.1):**
- ~4.776 LOC C# em `src/`
- Tech stack: C#/.NET 10, ICorDebug COM interop, libdbgshim.so 10.0.x, xUnit, ModelContextProtocol SDK
- 14 testes de integração automatizados passando (`dotnet test`)
- HelloDebug com 21 seções de teste cobrindo todos os padrões .NET
- MCP server registrado como `debugger-net` via `claude mcp add`

**Problema raiz original:** netcoredbg crasha com SIGSEGV no kernel Linux 6.12+ por race condition em `ManagedDebuggerHelpers::Startup(IUnknown*)`. Resolvido completamente via ICorDebug direto.

**Nova arquitetura:**
```
Claude Code → MCP Server (C#/stdio) → Debug Engine (C#) → libdbgshim.so + ICorDebug → .NET Process
```

**Componentes:**
- `DebuggerNetMcp.Core` — engine de debug em C# (ICorDebug, PdbReader, VariableReader, DotnetDebugger)
- `DebuggerNetMcp.Mcp` — console app C# expondo tools via MCP SDK oficial
- `DebuggerNetMcp.Tests` — xUnit com 14 testes de integração

**Ambiente de referência:**
- Kernel: Linux 6.17.0-14-generic
- .NET: 10.0.0 (target), runtimes 8.0.22 e 9.0.11 disponíveis
- libdbgshim.so: `~/.local/bin/` (versão 10.0.14 do pacote nightly)
- Projeto de teste: `tests/HelloDebug/` (net10.0, 21 seções)

## Constraints

- **Tech Stack**: C# (.NET 10) — sem Python, sem netcoredbg
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
| C# + COM (não Go ou Rust) | .NET 10 tem acesso nativo a COM interfaces ICorDebug, mesmo runtime alvo | ✓ Good |
| ICorDebugCode.CreateBreakpoint com ilOffset exato | fn.CreateBreakpoint() usava offset 0 — JIT do .NET 10 ignorava | ✓ Good |
| BreakpointTokenToId chave composta (token,offset) | Multiple BPs em mesmo método compartilhavam key → IDs errados | ✓ Good |
| StepRange com PDB ao invés de Step() | Step() não localiza PDB no .NET 10 → single instruction | ✓ Good |
| Event channel sempre recriado em LaunchAsync | Evita Reader.Completion falso-positivo com itens buffered | ✓ Good |
| PEReader ao invés de IMetaDataImport | IMetaDataImport COM Interop não funciona no Linux | ✓ Good |
| SuppressExitProcess flag em DisconnectAsync | Evita que ExitProcess callback do processo antigo corrompa o canal da nova sessão | ✓ Good |
| DisconnectAsync no início de LaunchAsync | Garante que _loadedModules e _eventChannel estejam limpos antes de nova sessão | ✓ Good |

---
*Last updated: 2026-02-25 after v1.1 milestone*
