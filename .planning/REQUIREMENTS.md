# Requirements: DebuggerNetMcp (Reescrita C#)

**Defined:** 2026-02-22
**Core Value:** Debug .NET funciona de forma confiável no kernel Linux 6.12+ sem workarounds frágeis

## v1 Requirements

### Infrastructure

- [ ] **INFRA-01**: Deletar todo o código Python existente (src/, pyproject.toml, uv.lock, requirements*.txt)
- [ ] **INFRA-02**: Criar solution .NET 10 com 4 projetos: DebuggerNetMcp.Native (C/CMake), DebuggerNetMcp.Core (class library), DebuggerNetMcp.Mcp (console app), DebuggerNetMcp.Tests (xUnit)
- [ ] **INFRA-03**: global.json com SDK 10.0.0, rollForward latestMinor
- [ ] **INFRA-04**: build.sh que compila Native com CMake, copia libdotnetdbg.so e executa dotnet build -c Release
- [ ] **INFRA-05**: install.sh que registra o MCP no Claude Code via `claude mcp add`

### Native (C/ptrace)

- [x] **NATIVE-01**: ptrace_wrapper.c com PTRACE_SEIZE — funções: dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait
- [x] **NATIVE-02**: CMakeLists.txt que compila como libdotnetdbg.so

### Interop (C#/COM)

- [ ] **INTEROP-01**: DbgShimInterop.cs com descoberta dinâmica de libdbgshim.so via NativeLibrary.Load() — busca em DOTNET_ROOT e /usr/share/dotnet
- [ ] **INTEROP-02**: ICorDebug.cs com interfaces COM completas e GUIDs corretos: ICorDebug, ICorDebugProcess, ICorDebugThread, ICorDebugFrame, ICorDebugILFrame, ICorDebugFunction, ICorDebugModule, ICorDebugValue, ICorDebugGenericValue, ICorDebugStringValue, ICorDebugObjectValue, ICorDebugArrayValue, ICorDebugBreakpoint, ICorDebugFunctionBreakpoint, ICorDebugStepper, ICorDebugManagedCallback, ICorDebugManagedCallback2

### Engine (C#/debug)

- [ ] **ENGINE-01**: Models — BreakpointInfo, StackFrameInfo, VariableInfo, EvalResult, hierarquia de DebugEvent (StoppedEvent, BreakpointHitEvent, ExceptionEvent, ExitedEvent, OutputEvent)
- [ ] **ENGINE-02**: PdbReader.cs — lê Portable PDB embutido ou separado, mapeia (arquivo, linha) ↔ (methodToken, ilOffset) usando System.Reflection.Metadata
- [ ] **ENGINE-03**: VariableReader.cs — lê ICorDebugValue recursivamente com limite de profundidade 3 (primitivos, strings, arrays, objetos)
- [ ] **ENGINE-04**: DotnetDebugger.cs — LaunchAsync (dotnet build -c Debug + RegisterForRuntimeStartup) e AttachAsync
- [ ] **ENGINE-05**: DotnetDebugger.cs — controle de execução: ContinueAsync, StepOverAsync, StepIntoAsync, StepOutAsync, PauseAsync
- [ ] **ENGINE-06**: DotnetDebugger.cs — breakpoints: SetBreakpointAsync(file, line), RemoveBreakpointAsync(id)
- [ ] **ENGINE-07**: DotnetDebugger.cs — inspeção: GetStackTraceAsync, GetLocalsAsync, EvaluateAsync
- [ ] **ENGINE-08**: Thread dedicada para ICorDebug + Channel<DebugEvent> para comunicação async com as tools MCP

### MCP Server (C#)

- [ ] **MCP-01**: Program.cs com MCP server via stdio usando NuGet ModelContextProtocol, DI com DotnetDebugger singleton
- [ ] **MCP-02**: DebuggerTools.cs — 14 tools com [McpServerTool] e [Description] em inglês: debug_launch, debug_attach, debug_set_breakpoint, debug_remove_breakpoint, debug_continue, debug_step_over, debug_step_into, debug_step_out, debug_variables, debug_evaluate, debug_stacktrace, debug_pause, debug_disconnect, debug_status

### Tests

- [ ] **TEST-01**: TestApps/HelloDebug/Program.cs — app de teste com variáveis primitivas, objetos complexos, listas/arrays e exceção capturada
- [ ] **TEST-02**: PdbReaderTests.cs — testa mapeamento de linhas para o HelloDebug
- [ ] **TEST-03**: DebuggerIntegrationTests.cs — fluxo completo: launch → breakpoint → variables → continue → exit

### Documentation

- [ ] **DOCS-01**: README.md reescrito — nova arquitetura (diagrama ASCII), pré-requisitos, localização do libdbgshim.so, build.sh, install.sh, exemplos de todas as tools, troubleshooting

## v2 Requirements

### Extended Features

- **EXT-01**: Suporte a Windows (além de Linux)
- **EXT-02**: Avaliação de expressões C# via ICorDebug (atualmente limitado a variáveis locais)
- **EXT-03**: Visualização de exceções não-capturadas com stack trace completo
- **EXT-04**: Hot reload de assemblies durante debug session

## Out of Scope

| Feature | Reason |
|---------|--------|
| netcoredbg | Causa raiz do problema — eliminado completamente |
| Python | Eliminado completamente; tudo em C# e C |
| DAP (Debug Adapter Protocol) | ICorDebug direto elimina essa camada intermediária |
| PTRACE_ATTACH | Usar apenas PTRACE_SEIZE para compatibilidade kernel 6.12+ |
| Paths hardcoded para libdbgshim.so | Sempre descoberta dinâmica via NativeLibrary.Load() |
| Suporte Windows | Foco no problema Linux kernel 6.12+ |
| GUI debugger | Ferramenta de linha de comando/MCP apenas |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01 | Phase 1 | Pending |
| INFRA-02 | Phase 1 | Pending |
| INFRA-03 | Phase 1 | Pending |
| INFRA-04 | Phase 1 | Pending |
| INFRA-05 | Phase 1 | Pending |
| NATIVE-01 | Phase 1 | Complete |
| NATIVE-02 | Phase 1 | Complete |
| INTEROP-01 | Phase 2 | Pending |
| INTEROP-02 | Phase 2 | Pending |
| ENGINE-01 | Phase 2 | Pending |
| ENGINE-02 | Phase 2 | Pending |
| ENGINE-03 | Phase 2 | Pending |
| ENGINE-04 | Phase 3 | Pending |
| ENGINE-05 | Phase 3 | Pending |
| ENGINE-06 | Phase 3 | Pending |
| ENGINE-07 | Phase 3 | Pending |
| ENGINE-08 | Phase 3 | Pending |
| MCP-01 | Phase 4 | Pending |
| MCP-02 | Phase 4 | Pending |
| TEST-01 | Phase 4 | Pending |
| TEST-02 | Phase 5 | Pending |
| TEST-03 | Phase 5 | Pending |
| DOCS-01 | Phase 5 | Pending |

**Coverage:**
- v1 requirements: 23 total
- Mapped to phases: 23
- Unmapped: 0 ✓

---
*Requirements defined: 2026-02-22*
*Last updated: 2026-02-22 — NATIVE-01, NATIVE-02 complete (01-02-PLAN.md executed)*
