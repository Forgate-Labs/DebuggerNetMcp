# Roadmap: DebuggerNetMcp (C# Rewrite)

## Overview

Complete rewrite from Python/netcoredbg to C#/ICorDebug. Phase 1 wipes the Python codebase and establishes the C# solution with a native ptrace wrapper. Phase 2 builds the COM interop layer and engine foundations (PdbReader, VariableReader, models). Phase 3 implements the full DotnetDebugger engine — the hardest phase. Phase 4 wires everything into an MCP server exposing 14 tools. Phase 5 validates with tests and rewrites documentation.

## Phases

- [x] **Phase 1: Foundation** - Delete Python, create C# solution + CMake native ptrace wrapper (completed 2026-02-22)
- [x] **Phase 2: Interop + Engine Foundation** - COM interfaces, models, PdbReader, VariableReader (completed 2026-02-22)
- [ ] **Phase 3: Debug Engine** - DotnetDebugger.cs complete (launch, step, breakpoints, inspect, async channel)
- [ ] **Phase 4: MCP Server** - 14 tools via ModelContextProtocol SDK + HelloDebug test app
- [ ] **Phase 5: Tests + Docs** - xUnit integration tests + README rewrite

## Phase Details

### Phase 1: Foundation
**Goal**: Python code is gone; a buildable C# solution with a working native ptrace wrapper exists
**Depends on**: Nothing (first phase)
**Requirements**: INFRA-01, INFRA-02, INFRA-03, INFRA-04, INFRA-05, NATIVE-01, NATIVE-02
**Success Criteria** (what must be TRUE):
  1. `git status` shows no Python files (src/, pyproject.toml, uv.lock removed)
  2. `dotnet build` completes without errors across all 4 projects
  3. `cmake --build` produces libdotnetdbg.so containing dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait symbols
  4. `build.sh` compiles native + managed in a single command
  5. `install.sh` registers the MCP server in Claude Code via `claude mcp add`
**Plans**: 3 plans

Plans:
- [ ] 01-01-PLAN.md — Delete Python artifacts + scaffold .NET 10 solution (INFRA-01, INFRA-02, INFRA-03)
- [x] 01-02-PLAN.md — CMake native ptrace wrapper library (NATIVE-01, NATIVE-02)
- [ ] 01-03-PLAN.md — build.sh + install.sh orchestration scripts (INFRA-04, INFRA-05)

### Phase 2: Interop + Engine Foundation
**Goal**: COM interop layer and engine building blocks compile and can be loaded against a real libdbgshim.so
**Depends on**: Phase 1
**Requirements**: INTEROP-01, INTEROP-02, ENGINE-01, ENGINE-02, ENGINE-03
**Success Criteria** (what must be TRUE):
  1. DbgShimInterop discovers libdbgshim.so dynamically at runtime (no hardcoded path)
  2. All ICorDebug COM interfaces compile with correct GUIDs and vtable layouts
  3. PdbReader maps a source line in HelloDebug to a valid (methodToken, ilOffset) pair
  4. VariableReader returns primitive, string, array, and object values from a live ICorDebugValue
**Plans**: 3 plans

Plans:
- [ ] 02-01-PLAN.md — Models.cs: BreakpointInfo, StackFrameInfo, VariableInfo, EvalResult, DebugEvent hierarchy (ENGINE-01)
- [ ] 02-02-PLAN.md — DbgShimInterop.cs + ICorDebug.cs: dynamic libdbgshim.so loading and all 17 COM interfaces (INTEROP-01, INTEROP-02)
- [ ] 02-03-PLAN.md — PdbReader.cs + VariableReader.cs: source-line mapping and recursive ICorDebugValue inspection (ENGINE-02, ENGINE-03)

### Phase 3: Debug Engine
**Goal**: DotnetDebugger.cs can launch a .NET app, hit breakpoints, step, inspect variables, and exit cleanly on kernel 6.12+
**Depends on**: Phase 2
**Requirements**: ENGINE-04, ENGINE-05, ENGINE-06, ENGINE-07, ENGINE-08
**Success Criteria** (what must be TRUE):
  1. `LaunchAsync` builds and starts a .NET app; process stops at entry point without SIGSEGV
  2. `SetBreakpointAsync(file, line)` halts execution at the correct source line
  3. `StepOverAsync`, `StepIntoAsync`, `StepOutAsync` advance execution one step without hanging
  4. `GetLocalsAsync` returns local variables with correct names and values at a breakpoint
  5. ICorDebug callbacks run on a dedicated thread; Channel<DebugEvent> delivers events to async callers without deadlocks
**Plans**: TBD

### Phase 4: MCP Server
**Goal**: Claude Code can invoke all 14 debug tools via stdio MCP and drive a full debug session against HelloDebug
**Depends on**: Phase 3
**Requirements**: MCP-01, MCP-02, TEST-01
**Success Criteria** (what must be TRUE):
  1. `dotnet run --project DebuggerNetMcp.Mcp` starts and accepts MCP requests over stdio
  2. All 14 tools (debug_launch, debug_attach, debug_set_breakpoint, debug_remove_breakpoint, debug_continue, debug_step_over, debug_step_into, debug_step_out, debug_variables, debug_evaluate, debug_stacktrace, debug_pause, debug_disconnect, debug_status) are callable and return structured results
  3. HelloDebug test app exposes primitive variables, complex objects, arrays, and a caught exception for tool verification
**Plans**: TBD

### Phase 5: Tests + Docs
**Goal**: Integration tests pass end-to-end; README describes the new architecture and how to build, install, and use the server
**Depends on**: Phase 4
**Requirements**: TEST-02, TEST-03, DOCS-01
**Success Criteria** (what must be TRUE):
  1. `dotnet test` runs PdbReaderTests and DebuggerIntegrationTests with all tests passing
  2. Integration test executes full flow: launch -> set breakpoint -> variables -> continue -> exit without manual intervention
  3. README contains architecture diagram, prerequisites, build.sh/install.sh instructions, and examples for all 14 tools
**Plans**: TBD

## Progress

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation | 3/3 | Complete   | 2026-02-22 |
| 2. Interop + Engine Foundation | 3/3 | Complete   | 2026-02-22 |
| 3. Debug Engine | 0/TBD | Not started | - |
| 4. MCP Server | 0/TBD | Not started | - |
| 5. Tests + Docs | 0/TBD | Not started | - |
