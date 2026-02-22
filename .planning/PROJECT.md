# DebuggerNetMcp (Reescrita C#)

## What This Is

MCP server para debug interativo de aplicações .NET Core no Linux, expondo 14 ferramentas de debug para o Claude Code via stdio. O projeto está sendo reescrito do zero: eliminando a dependência do netcoredbg (que crasha no kernel Linux 6.12+ com SIGSEGV) e reimplementando o debug engine diretamente em C#/.NET 10 usando ICorDebug e libdbgshim.so nativos.

## Core Value

O debug deve funcionar de forma confiável no kernel Linux 6.12+ sem workarounds frágeis — o Claude Code consegue lançar, pausar, inspecionar variáveis e navegar por código .NET com uma única ferramenta.

## Requirements

### Validated

- ✓ 14 debug tools expostas via MCP (launch, attach, breakpoints, step, variables, evaluate, stacktrace, pause, disconnect, status) — existente em Python
- ✓ Comunicação MCP via stdio — existente em Python
- ✓ Integração com libdbgshim.so para runtime startup callbacks — existente via netcoredbg
- ✓ Workaround strace para kernel >= 6.12 — existente, mas frágil

### Active

- [ ] MCP server reimplementado em C#/.NET 10 (sem Python)
- [ ] Debug engine usando ICorDebug diretamente (sem netcoredbg)
- [ ] Wrapper ptrace nativo em C usando PTRACE_SEIZE (compatível kernel 6.12+)
- [ ] Descoberta dinâmica de libdbgshim.so via NativeLibrary.Load()
- [ ] Leitura de Portable PDB para mapear linhas ↔ IL offsets (breakpoints e stacktrace)
- [ ] Leitura recursiva de variáveis via ICorDebugValue (primitivos, strings, objetos, arrays)
- [ ] Thread dedicada para ICorDebug com Channel<DebugEvent> para comunicação async
- [ ] Build system (CMake para native + dotnet build para C#)
- [ ] Projeto xUnit com testes de integração end-to-end
- [ ] README atualizado com nova arquitetura

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
| ICorDebug direto (sem DAP) | Elimina camada intermediária netcoredbg, resolve race condition na raiz | — Pending |
| PTRACE_SEIZE vs PTRACE_ATTACH | PTRACE_SEIZE é compatível com kernel 6.12+, não causa SIGSEGV | — Pending |
| Channel<DebugEvent> para async | Desacopla thread do ICorDebug das tools async do MCP | — Pending |
| Thread dedicada para ICorDebug | Requisito do COM — todos os acessos devem ser na mesma thread | — Pending |
| C# + C (não Go ou Rust) | .NET 10 tem acesso nativo a COM interfaces ICorDebug, mesmo runtime alvo | — Pending |

---
*Last updated: 2026-02-22 after initialization*
