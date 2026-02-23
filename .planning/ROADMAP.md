# Roadmap: DebuggerNetMcp (C# Rewrite)

## Milestones

- ✅ **v1.0 C# Rewrite** - Phases 1-4 (shipped 2026-02-22)
- 🚧 **v1.1 Complete .NET Debug Coverage** - Phases 5-9 (in progress)

## Phases

<details>
<summary>✅ v1.0 C# Rewrite (Phases 1-4) - SHIPPED 2026-02-22</summary>

Complete rewrite from Python/netcoredbg to C#/ICorDebug. Phase 1 wipes the Python codebase and establishes the C# solution with a native ptrace wrapper. Phase 2 builds the COM interop layer and engine foundations (PdbReader, VariableReader, models). Phase 3 implements the full DotnetDebugger engine. Phase 4 wires everything into an MCP server exposing 14 tools.

- [x] **Phase 1: Foundation** - Delete Python, create C# solution + CMake native ptrace wrapper (completed 2026-02-22)
- [x] **Phase 2: Interop + Engine Foundation** - COM interfaces, models, PdbReader, VariableReader (completed 2026-02-22)
- [x] **Phase 3: Debug Engine** - DotnetDebugger.cs complete (launch, step, breakpoints, inspect, async channel) (completed 2026-02-22)
- [x] **Phase 4: MCP Server** - 14 tools via ModelContextProtocol SDK + HelloDebug test app (completed 2026-02-23)

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
- [x] 01-01-PLAN.md — Delete Python artifacts + scaffold .NET 10 solution (INFRA-01, INFRA-02, INFRA-03)
- [x] 01-02-PLAN.md — CMake native ptrace wrapper library (NATIVE-01, NATIVE-02)
- [x] 01-03-PLAN.md — build.sh + install.sh orchestration scripts (INFRA-04, INFRA-05)

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
- [x] 02-01-PLAN.md — Models.cs: BreakpointInfo, StackFrameInfo, VariableInfo, EvalResult, DebugEvent hierarchy (ENGINE-01)
- [x] 02-02-PLAN.md — DbgShimInterop.cs + ICorDebug.cs: dynamic libdbgshim.so loading and all 17 COM interfaces (INTEROP-01, INTEROP-02)
- [x] 02-03-PLAN.md — PdbReader.cs + VariableReader.cs: source-line mapping and recursive ICorDebugValue inspection (ENGINE-02, ENGINE-03)

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
**Plans**: 5 plans

Plans:
- [x] 03-01-PLAN.md — ICorDebugClass.GetToken + IMetaDataImportMinimal: COM interop extensions for object field inspection (ENGINE-08)
- [x] 03-02-PLAN.md — ManagedCallbackHandler: [GeneratedComClass] sink implementing all 34 ICorDebug callback methods (ENGINE-08)
- [x] 03-03-PLAN.md — DotnetDebugger.cs core: Channel, dedicated thread, LaunchAsync, AttachAsync, DisconnectAsync (ENGINE-04, ENGINE-08)
- [x] 03-04-PLAN.md — DotnetDebugger.cs execution control: SetBreakpointAsync, RemoveBreakpointAsync, StepOver/Into/Out, Continue, Pause (ENGINE-05, ENGINE-06)
- [x] 03-05-PLAN.md — DotnetDebugger.cs inspection: GetStackTraceAsync, GetLocalsAsync, EvaluateAsync + VariableReader.ReadObject field enumeration (ENGINE-07)

### Phase 4: MCP Server
**Goal**: Claude Code can invoke all 14 debug tools via stdio MCP and drive a full debug session against HelloDebug
**Depends on**: Phase 3
**Requirements**: MCP-01, MCP-02, TEST-01
**Success Criteria** (what must be TRUE):
  1. `dotnet run --project DebuggerNetMcp.Mcp` starts and accepts MCP requests over stdio
  2. All 14 tools are callable and return structured results
  3. HelloDebug test app exposes primitive variables, complex objects, arrays, and a caught exception for tool verification
**Plans**: 2 plans

Plans:
- [x] 04-01-PLAN.md — Add ModelContextProtocol package + Program.cs MCP host + verify HelloDebug (MCP-01, TEST-01)
- [x] 04-02-PLAN.md — DebuggerTools.cs: 14 [McpServerTool] methods wrapping DotnetDebugger (MCP-02)

</details>

### 🚧 v1.1 Complete .NET Debug Coverage (In Progress)

**Milestone Goal:** Expand the debug engine to cover all major .NET patterns — structs, enums, nullable, closures, iterators, static fields, circular references, unhandled exceptions, multi-threading, attach, and reverse PDB lookup — with xUnit integration tests and complete documentation.

- [x] **Phase 5: Type System** - Structs, enums, Nullable<T>, and static fields readable via debug_variables/debug_evaluate (completed 2026-02-23)
- [x] **Phase 6: Closures, Iterators & Object Graph** - Lambda captures, yield return state machines, circular reference detection, computed properties (completed 2026-02-23)
- [ ] **Phase 7: Exceptions, Threading & Attach** - Unhandled exception events, first-chance notifications, multi-thread inspection, debug_attach by PID
- [ ] **Phase 8: Stack Trace & dotnet test** - Reverse PDB lookup for readable stack frames, debug_launch for xUnit test processes
- [ ] **Phase 9: Tests & Documentation** - xUnit integration tests covering all scenarios, README rewrite

### Phase 5: Type System
**Goal**: Users can inspect struct, enum, Nullable<T>, and static field values via debug_variables and debug_evaluate — the type system reads correctly for all .NET value types
**Depends on**: Phase 4
**Requirements**: TYPE-01, TYPE-02, TYPE-03, TYPE-04
**Success Criteria** (what must be TRUE):
  1. `debug_variables` at a breakpoint returns a struct's fields with correct types and values (not "unreadable" or empty)
  2. `debug_variables` shows an enum variable as "EnumName.MemberName" string, not a raw integer
  3. `debug_variables` shows a Nullable<T> with HasValue=true as the unwrapped value; with HasValue=false as "null"
  4. `debug_evaluate` or `debug_variables` returns the current value of a static field from a class
**Plans**: 3 plans

Plans:
- [ ] 05-01-PLAN.md — Enum detection (IsEnumType/ReadEnumValue) + Nullable<T> unwrapping in VariableReader (TYPE-02, TYPE-03)
- [ ] 05-02-PLAN.md — ICorDebugClass.GetStaticFieldValue + static field reading in VariableReader + GetLocalsAsync/EvaluateAsync (TYPE-04)
- [ ] 05-03-PLAN.md — HelloDebug sections 13-16 (struct, enum, Nullable, static) + live verification checkpoint (TYPE-01)

### Phase 6: Closures, Iterators & Object Graph
**Goal**: Users can inspect lambda-captured variables and iterator state; VariableReader handles circular object graphs without crashing
**Depends on**: Phase 5
**Requirements**: CLSR-01, CLSR-02, GRAPH-01, GRAPH-02, TEST-08
**Success Criteria** (what must be TRUE):
  1. `debug_variables` inside a lambda shows captured variables with clean names (no compiler-generated suffixes)
  2. `debug_variables` on an iterator shows the Current value and the state machine's internal state field
  3. `debug_variables` on an object with a circular reference returns a "circular reference" marker instead of crashing with a stack overflow
  4. Properties without a backing field in the PE are reported as "<computed>" rather than missing from the output
  5. HelloDebug sections 13-19 covering struct, enum, nullable, closure, iterator, threading, and circular ref are in place and used by manual verification
**Plans**: 3 plans

Plans:
- [ ] 06-01-PLAN.md — Closure detection (>b__ method) + iterator Current field in GetLocalsAsync (CLSR-01, CLSR-02)
- [ ] 06-02-PLAN.md — Circular reference detection (HashSet<ulong> visited) + computed property reporting in VariableReader (GRAPH-01, GRAPH-02)
- [ ] 06-03-PLAN.md — HelloDebug sections 17-19 (closure, iterator, circular ref) + live verification checkpoint (TEST-08)

### Phase 7: Exceptions, Threading & Attach
**Goal**: Users receive exception events for unhandled errors, can inspect all threads, and can attach the debugger to a running .NET process by PID
**Depends on**: Phase 6
**Requirements**: EXCP-01, EXCP-02, THRD-01, THRD-02, THRD-03, ATCH-01
**Success Criteria** (what must be TRUE):
  1. When a .NET process throws an unhandled exception, a second-chance ExceptionEvent is delivered with the exception type and message before the process exits
  2. First-chance exception notifications can be enabled via a debug_launch option; a matching event is delivered for each thrown exception
  3. `debug_variables` with a thread_id argument returns locals for that specific thread, not the main thread
  4. `debug_stacktrace` returns frames for all active threads when called without a thread filter, or for a specific thread when given a thread ID
  5. `debug_pause` stops all threads in the process, not just the thread currently in the callback
  6. `debug_attach(pid)` connects to a running .NET process and returns state="attached" with process information
**Plans**: 3 plans

Plans:
- [ ] 07-01-PLAN.md — Exception type/message extraction (TryReadExceptionInfo) + first-chance flag + double-reporting guard (EXCP-01, EXCP-02)
- [ ] 07-02-PLAN.md — Multi-thread inspection: GetAllThreads + optional threadId on debug_variables/debug_stacktrace (THRD-01, THRD-02, THRD-03)
- [ ] 07-03-PLAN.md — AttachAsync confirmation via TCS + HelloDebug sections 20-21 + live verification checkpoint (ATCH-01)

### Phase 8: Stack Trace & dotnet test
**Goal**: Stack frames show human-readable source locations ("Program.cs:57") and Claude Code can debug xUnit test methods with breakpoints
**Depends on**: Phase 7
**Requirements**: STKT-01, STKT-02, DTEST-01, DTEST-02
**Success Criteria** (what must be TRUE):
  1. PdbReader.ReverseLookup(methodToken, ilOffset) returns the correct source file path and line number using PDB sequence points
  2. `debug_stacktrace` output includes "sourceFile:line" for every frame where a PDB is available (no raw hex tokens in normal output)
  3. `debug_launch` with an xUnit project path runs `dotnet test` in debug mode and stops at the CreateProcess event
  4. A breakpoint set inside a [Fact] test method is hit during `dotnet test` execution and `debug_variables` returns the test's local values
**Plans**: TBD

### Phase 9: Tests & Documentation
**Goal**: Automated xUnit integration tests cover all debug scenarios end-to-end; README gives a new contributor everything needed to build, install, and use the server
**Depends on**: Phase 8
**Requirements**: TEST-02, TEST-03, TEST-09, DOCS-01
**Success Criteria** (what must be TRUE):
  1. `dotnet test` runs PdbReaderTests (forward and reverse lookup) with all assertions passing
  2. `dotnet test` runs DebuggerIntegrationTests covering launch, breakpoint, variables, step, and exit — all passing without manual intervention
  3. Integration tests cover unhandled exceptions, multi-thread inspection, and process attach scenarios — all passing
  4. README contains an ASCII architecture diagram, prerequisites (libdbgshim.so location), build.sh/install.sh steps, and usage examples for all 14 tools
**Plans**: TBD

## Progress

| Phase | Milestone | Plans Complete | Status | Completed |
|-------|-----------|----------------|--------|-----------|
| 1. Foundation | v1.0 | 3/3 | Complete | 2026-02-22 |
| 2. Interop + Engine Foundation | v1.0 | 3/3 | Complete | 2026-02-22 |
| 3. Debug Engine | v1.0 | 5/5 | Complete | 2026-02-22 |
| 4. MCP Server | v1.0 | 2/2 | Complete | 2026-02-23 |
| 5. Type System | v1.1 | 3/3 | Complete | 2026-02-23 |
| 6. Closures, Iterators & Object Graph | 3/3 | Complete   | 2026-02-23 | - |
| 7. Exceptions, Threading & Attach | 1/3 | In Progress|  | - |
| 8. Stack Trace & dotnet test | v1.1 | 0/TBD | Not started | - |
| 9. Tests & Documentation | v1.1 | 0/TBD | Not started | - |
