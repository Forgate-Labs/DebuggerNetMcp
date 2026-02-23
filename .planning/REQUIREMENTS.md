# Requirements: DebuggerNetMcp

**Defined:** 2026-02-22
**Core Value:** Debug .NET funciona de forma confiável no kernel Linux 6.12+ sem workarounds frágeis

---

## v1.0 Requirements (Complete)

### Infrastructure
- [x] **INFRA-01**: Deletar todo o código Python existente (src/, pyproject.toml, uv.lock, requirements*.txt)
- [x] **INFRA-02**: Criar solution .NET 10 com 4 projetos: DebuggerNetMcp.Native (C/CMake), DebuggerNetMcp.Core (class library), DebuggerNetMcp.Mcp (console app), DebuggerNetMcp.Tests (xUnit)
- [x] **INFRA-03**: global.json com SDK 10.0.0, rollForward latestMinor
- [x] **INFRA-04**: build.sh que compila Native com CMake, copia libdotnetdbg.so e executa dotnet build -c Release
- [x] **INFRA-05**: install.sh que registra o MCP no Claude Code via `claude mcp add`

### Native (C/ptrace)
- [x] **NATIVE-01**: ptrace_wrapper.c com PTRACE_SEIZE — funções: dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait
- [x] **NATIVE-02**: CMakeLists.txt que compila como libdotnetdbg.so

### Interop (C#/COM)
- [x] **INTEROP-01**: DbgShimInterop.cs com descoberta dinâmica de libdbgshim.so via NativeLibrary.Load() — busca em DOTNET_ROOT e /usr/share/dotnet
- [x] **INTEROP-02**: ICorDebug.cs com interfaces COM completas e GUIDs corretos: ICorDebug, ICorDebugProcess, ICorDebugThread, ICorDebugFrame, ICorDebugILFrame, ICorDebugFunction, ICorDebugModule, ICorDebugValue, ICorDebugGenericValue, ICorDebugStringValue, ICorDebugObjectValue, ICorDebugArrayValue, ICorDebugBreakpoint, ICorDebugFunctionBreakpoint, ICorDebugStepper, ICorDebugManagedCallback, ICorDebugManagedCallback2

### Engine (C#/debug)
- [x] **ENGINE-01**: Models — BreakpointInfo, StackFrameInfo, VariableInfo, EvalResult, hierarquia de DebugEvent (StoppedEvent, BreakpointHitEvent, ExceptionEvent, ExitedEvent, OutputEvent)
- [x] **ENGINE-02**: PdbReader.cs — lê Portable PDB embutido ou separado, mapeia (arquivo, linha) ↔ (methodToken, ilOffset) usando System.Reflection.Metadata
- [x] **ENGINE-03**: VariableReader.cs — lê ICorDebugValue recursivamente com limite de profundidade 3 (primitivos, strings, arrays, objetos)
- [x] **ENGINE-04**: DotnetDebugger.cs — LaunchAsync (dotnet build -c Debug + RegisterForRuntimeStartup) e AttachAsync
- [x] **ENGINE-05**: DotnetDebugger.cs — controle de execução: ContinueAsync, StepOverAsync, StepIntoAsync, StepOutAsync, PauseAsync
- [x] **ENGINE-06**: DotnetDebugger.cs — breakpoints: SetBreakpointAsync(file, line), RemoveBreakpointAsync(id)
- [x] **ENGINE-07**: DotnetDebugger.cs — inspeção: GetStackTraceAsync, GetLocalsAsync, EvaluateAsync
- [x] **ENGINE-08**: Thread dedicada para ICorDebug + Channel<DebugEvent> para comunicação async com as tools MCP

### MCP Server (C#)
- [x] **MCP-01**: Program.cs com MCP server via stdio usando NuGet ModelContextProtocol, DI com DotnetDebugger singleton
- [x] **MCP-02**: DebuggerTools.cs — 14 tools com [McpServerTool] e [Description] em inglês: debug_launch, debug_attach, debug_set_breakpoint, debug_remove_breakpoint, debug_continue, debug_step_over, debug_step_into, debug_step_out, debug_variables, debug_evaluate, debug_stacktrace, debug_pause, debug_disconnect, debug_status

### Tests (v1.0)
- [x] **TEST-01**: tests/HelloDebug/Program.cs — app de teste com seções 1-12: primitivos, strings, loop, coleções, objetos, Fibonacci (step-into), exceção capturada, async, linked list, step-over/out, múltiplos BPs, evaluate

---

## v1.1 Requirements

**Defined:** 2026-02-23

### Sistema de Tipos (TYPE)

- [x] **TYPE-01**: Debugger pode ler campos de structs (value types) como VariableInfo com tipo e valor corretos
- [x] **TYPE-02**: Debugger pode ler valores de enum como string "NomeDoEnum.Membro" (não apenas int)
- [x] **TYPE-03**: Debugger pode ler Nullable\<T\> — exibe valor quando HasValue=true, exibe "null" quando HasValue=false
- [x] **TYPE-04**: Debugger pode ler static fields de uma classe via debug_variables ou debug_evaluate

### Closures e Iterators (CLSR)

- [x] **CLSR-01**: Debugger exibe variáveis capturadas por lambdas (campos do compiler-generated Display class) com nomes limpos
- [x] **CLSR-02**: Debugger pode inspecionar estado de um iterator (yield return) — valor Current e estado interno da state machine

### Grafo de Objetos (GRAPH)

- [x] **GRAPH-01**: VariableReader detecta referências circulares e retorna marker "circular reference" sem stack overflow
- [x] **GRAPH-02**: Computed properties (sem backing field no PE) são reportadas com valor obtido via IL evaluation ou marcadas como "\<computed\>"

### Exceções (EXCP)

- [x] **EXCP-01**: Debugger notifica exceção não-capturada (second-chance) via ExceptionEvent com tipo e mensagem — processo não termina silenciosamente
- [x] **EXCP-02**: Debugger suporta first-chance exception notifications (opcional — configurável via debug_launch)

### Multi-threading (THRD)

- [ ] **THRD-01**: debug_variables retorna variáveis do thread correto quando múltiplos threads existem
- [ ] **THRD-02**: debug_stacktrace retorna stack de todos os threads ativos (ou thread especificado por ID)
- [ ] **THRD-03**: debug_pause interrompe todos os threads do processo (não apenas o thread principal)

### Process Attach (ATCH)

- [ ] **ATCH-01**: debug_attach(pid) conecta ao processo .NET já em execução e retorna state="attached" com informações do processo

### Stack Trace (STKT)

- [ ] **STKT-01**: PdbReader implementa reverse lookup — dado (methodToken, ilOffset) retorna (sourceFile, line) usando sequence points do PDB
- [ ] **STKT-02**: debug_stacktrace retorna frames com sourceFile e line legíveis (ex: "Program.cs:57") em vez de tokens hexadecimais

### dotnet test (DTEST)

- [ ] **DTEST-01**: debug_launch aceita projeto xUnit e lança `dotnet test` em modo debug — processo para no CreateProcess
- [ ] **DTEST-02**: Breakpoints dentro de métodos de teste xUnit ([Fact], [Theory]) são atingidos e variáveis inspecionáveis

### Testes de Integração (TEST)

- [ ] **TEST-02**: PdbReaderTests.cs — testa mapeamento source→IL e reverse IL→source para HelloDebug e HelloDebug v1.1
- [ ] **TEST-03**: DebuggerIntegrationTests.cs — fluxo completo: launch → breakpoint → variables → continue → exit sem intervenção manual
- [x] **TEST-08**: HelloDebug expandido com seções 13-19 cobrindo struct, enum, nullable, closure, iterator, threading, circular ref
- [ ] **TEST-09**: Integration tests para exceções não-capturadas, multi-threading e process attach

### Documentação (DOCS)

- [ ] **DOCS-01**: README.md reescrito — nova arquitetura (diagrama ASCII), pré-requisitos, localização do libdbgshim.so, build.sh, install.sh, exemplos de todas as tools, troubleshooting

---

## Future Requirements (v2.0+)

### Extended Features

- **EXT-01**: Suporte a Windows (além de Linux)
- **EXT-02**: Avaliação de expressões C# complexas via ICorDebug (ex: `person.Name.ToUpper()`)
- **EXT-03**: Hot reload de assemblies durante debug session
- **EXT-04**: Source maps para projetos multi-target (netX.Y + netstandard)

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
| Expression evaluation complexa | Requer ICorDebug Eval — muito complexo para v1.1 |

## Traceability

| Requirement | Phase | Status |
|-------------|-------|--------|
| INFRA-01..05 | Phase 1 | Complete |
| NATIVE-01..02 | Phase 1 | Complete |
| INTEROP-01..02 | Phase 2 | Complete |
| ENGINE-01..08 | Phase 2-3 | Complete |
| MCP-01..02 | Phase 4 | Complete |
| TEST-01 | Phase 4 | Complete |
| TYPE-01 | Phase 5 | Complete |
| TYPE-02 | Phase 5 | Complete |
| TYPE-03 | Phase 5 | Complete |
| TYPE-04 | Phase 5 | Complete |
| CLSR-01 | Phase 6 | Complete |
| CLSR-02 | Phase 6 | Complete |
| GRAPH-01 | Phase 6 | Complete |
| GRAPH-02 | Phase 6 | Complete |
| TEST-08 | Phase 6 | Complete |
| EXCP-01 | Phase 7 | Complete |
| EXCP-02 | Phase 7 | Complete |
| THRD-01 | Phase 7 | Pending |
| THRD-02 | Phase 7 | Pending |
| THRD-03 | Phase 7 | Pending |
| ATCH-01 | Phase 7 | Pending |
| STKT-01 | Phase 8 | Pending |
| STKT-02 | Phase 8 | Pending |
| DTEST-01 | Phase 8 | Pending |
| DTEST-02 | Phase 8 | Pending |
| TEST-02 | Phase 9 | Pending |
| TEST-03 | Phase 9 | Pending |
| TEST-09 | Phase 9 | Pending |
| DOCS-01 | Phase 9 | Pending |

**Coverage v1.1:**
- v1.1 requirements: 24 total
- Mapped to phases: 24
- Unmapped: 0 ✓

---
*Requirements defined: 2026-02-22*
*Last updated: 2026-02-23 — v1.1 requirements added (TYPE, CLSR, GRAPH, EXCP, THRD, ATCH, STKT, DTEST, TEST, DOCS); traceability updated for Phases 5-9*
