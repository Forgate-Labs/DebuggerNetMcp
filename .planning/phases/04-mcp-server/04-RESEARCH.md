# Phase 4: MCP Server - Research

**Researched:** 2026-02-22
**Domain:** ModelContextProtocol C# SDK, stdio MCP server, DI wiring, tool design
**Confidence:** HIGH

---

## Summary

Phase 4 wires the existing `DotnetDebugger` engine (Phase 3) into an MCP server that
Claude Code can talk to over stdio. The official C# SDK is `ModelContextProtocol`
(0.9.0-preview.2, released 2026-02-21). It uses `Microsoft.Extensions.Hosting` DI
conventions: `AddMcpServer().WithStdioServerTransport().WithTools<T>()` in Program.cs,
and `[McpServerToolType]` / `[McpServerTool]` attributes on tool methods. All 14 tools
map 1-to-1 onto `DotnetDebugger` async methods already implemented in Phase 3. The
`DotnetDebugger` singleton is registered in DI and injected into the tool class as a
constructor parameter (non-static class pattern).

The key design challenge is that some tools (debug_variables, debug_stacktrace,
debug_evaluate) require the debuggee to be in a STOPPED state. The correct pattern is
to call `WaitForEventAsync` before those methods if needed, or to require the caller
to have already issued `debug_set_breakpoint` + `debug_continue` and waited for a
`StoppedEvent`. Tool return values are serialized via `System.Text.Json` to a JSON
string returned from the tool method — the SDK wraps it in a `TextContentBlock`
automatically.

HelloDebug already exists (`tests/HelloDebug/Program.cs`) with 9 sections covering
primitives, strings, loops, collections, object graphs, step-into, exceptions, async,
and nested objects. It satisfies TEST-01 as-is. The csproj has `DebugType=portable`
and `Optimize=false`, which are required for PDB reading to work.

**Primary recommendation:** Use `WithTools<DebuggerTools>()` (explicit type, not
`WithToolsFromAssembly`) for clarity and AOT-friendliness. Register `DotnetDebugger`
as a singleton with `AddSingleton<DotnetDebugger>()`. All tool methods are instance
methods on a non-static `DebuggerTools` class injected with `DotnetDebugger` via
constructor.

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| MCP-01 | Program.cs with MCP server via stdio using NuGet ModelContextProtocol, DI with DotnetDebugger singleton | ModelContextProtocol 0.9.0-preview.2; AddMcpServer/WithStdioServerTransport/WithTools<T> pattern documented |
| MCP-02 | DebuggerTools.cs — 14 tools with [McpServerTool] and [Description] in English | All 14 DotnetDebugger methods confirmed with full signatures; McpServerTool/Name/Description attribute pattern confirmed |
| TEST-01 | TestApps/HelloDebug/Program.cs — app with primitives, complex objects, lists/arrays and caught exception | HelloDebug already exists at tests/HelloDebug/Program.cs with 9 sections covering all required scenarios |
</phase_requirements>

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| ModelContextProtocol | 0.9.0-preview.2 | MCP server framework: DI integration, stdio transport, tool attribute scanning | Official C# SDK, maintained in collaboration with Microsoft; only real option for .NET MCP |
| Microsoft.Extensions.Hosting | 10.0.3 (pulled transitively) | Generic host, DI container, lifetime management | Standard .NET hosting; MCP SDK depends on it |
| System.ComponentModel | inbox | `[Description]` attribute on tools and parameters | Used by MCP SDK to populate tool/parameter descriptions in protocol responses |
| System.Text.Json | inbox | Serialize tool results to JSON strings | Standard .NET JSON; no extra package needed |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Extensions.Logging.Console | 10.0.3 (transitively) | Route log output to stderr (not stdout) | Always — stdout is the MCP wire protocol |

### Installation
```bash
dotnet add src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj package ModelContextProtocol --prerelease
```

The `Microsoft.Extensions.Hosting` dependency is pulled automatically via
`ModelContextProtocol` → `ModelContextProtocol.Core` → hosting abstractions.

---

## Architecture Patterns

### Recommended File Structure for Phase 4

```
src/DebuggerNetMcp.Mcp/
├── Program.cs               # Host builder + DI + MCP registration
└── DebuggerTools.cs         # [McpServerToolType] class with 14 [McpServerTool] methods

tests/HelloDebug/
└── Program.cs               # Already complete — 9 debug sections (TEST-01 satisfied)
```

### Pattern 1: stdio MCP Server Program.cs

**What:** The host builder pattern wires MCP transport and DI in Program.cs.
**When to use:** Always — this is the only supported pattern for stdio MCP servers.

```csharp
// Source: https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

var builder = Host.CreateApplicationBuilder(args);

// CRITICAL: logs MUST go to stderr — stdout is the MCP wire protocol
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Register DotnetDebugger as singleton — one debug session per server process
builder.Services.AddSingleton<DotnetDebugger>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DebuggerTools>();

await builder.Build().RunAsync();
```

### Pattern 2: Non-Static Tool Class with Constructor Injection

**What:** `DebuggerTools` is a non-static class with `DotnetDebugger` injected via
constructor. This is preferred over static methods because the debugger singleton is
stateful and has a defined lifetime.
**When to use:** When tools share a singleton stateful service.

```csharp
// Source: https://github.com/modelcontextprotocol/csharp-sdk (WeatherTools sample)
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

[McpServerToolType]
public sealed class DebuggerTools(DotnetDebugger debugger)
{
    [McpServerTool(Name = "debug_launch"),
     Description("Build and launch a .NET project under the debugger.")]
    public async Task<string> Launch(
        [Description("Path to the .csproj file or project directory")] string projectPath,
        [Description("Path to the compiled .dll (e.g. bin/Debug/net10.0/App.dll)")] string appDllPath,
        CancellationToken ct)
    {
        await debugger.LaunchAsync(projectPath, appDllPath, ct);
        return JsonSerializer.Serialize(new { status = "launched", projectPath, appDllPath });
    }
}
```

**Key rules:**
- Constructor parameters resolved from DI (not exposed as tool parameters)
- `CancellationToken` injected automatically — always include it in every tool method
- `[Description]` on parameters provides JSON schema documentation to clients
- Return `Task<string>` — SDK wraps in `TextContentBlock` automatically
- `Name = "debug_launch"` overrides the default snake_case conversion from method name

### Pattern 3: Tool Naming Convention

**What:** The SDK auto-converts method names to `snake_case` by removing `Async` suffix
and applying `JsonNamingPolicy.SnakeCaseLower`. However, for this project the method
names in the class won't match the required tool names (e.g., method `Launch` vs.
tool `debug_launch`).

**Solution:** Always use `[McpServerTool(Name = "debug_xxx")]` explicitly on every tool.

| Method Name | Default SDK name | Required Tool Name | Use Name= |
|-------------|------------------|--------------------|-----------|
| `Launch` | `launch` | `debug_launch` | YES |
| `Attach` | `attach` | `debug_attach` | YES |
| `SetBreakpoint` | `set_breakpoint` | `debug_set_breakpoint` | YES |
| `RemoveBreakpoint` | `remove_breakpoint` | `debug_remove_breakpoint` | YES |
| `Continue` | `continue` | `debug_continue` | YES |
| `StepOver` | `step_over` | `debug_step_over` | YES |
| `StepInto` | `step_into` | `debug_step_into` | YES |
| `StepOut` | `step_out` | `debug_step_out` | YES |
| `GetVariables` | `get_variables` | `debug_variables` | YES |
| `Evaluate` | `evaluate` | `debug_evaluate` | YES |
| `GetStackTrace` | `get_stack_trace` | `debug_stacktrace` | YES |
| `Pause` | `pause` | `debug_pause` | YES |
| `Disconnect` | `disconnect` | `debug_disconnect` | YES |
| `GetStatus` | `get_status` | `debug_status` | YES |

### Pattern 4: debug_status — State Reporting

**What:** The `DotnetDebugger` does not expose a `State` property. Status must be
inferred from observable state: whether `_process` is set is private. The `DebuggerTools`
class needs to track session state alongside the debugger.

**Solution:** Add a simple `DebuggerSession` wrapper (or track state in `DebuggerTools`)
using an enum or string field:

```csharp
// In DebuggerTools or a thin DebuggerSession class
private string _state = "idle";    // idle | running | stopped | exited

// After LaunchAsync succeeds → state = "running"
// After WaitForEventAsync returns StoppedEvent → state = "stopped"
// After DisconnectAsync → state = "idle"
// After ExitedEvent → state = "exited"
```

Alternatively, expose a `State` property on `DotnetDebugger` (minor extension to Phase 3
engine). The planner should decide whether to add it to the engine or track in the tool
layer.

### Pattern 5: Tools Requiring Stopped State

**What:** `debug_variables`, `debug_stacktrace`, and `debug_evaluate` call ICorDebug
APIs that require the process to be stopped. Calling them while running produces
HRESULT errors.

**Solution (recommended):** These tools should NOT internally call `WaitForEventAsync`.
Instead they should return an error string if the process is not stopped. The user
(Claude) is responsible for waiting for a `StoppedEvent` by calling `debug_continue`
and reading the response, or by using a `debug_wait_for_stop` pattern (not in scope).

```csharp
[McpServerTool(Name = "debug_variables"), Description("...")]
public async Task<string> GetVariables(CancellationToken ct)
{
    try
    {
        var locals = await debugger.GetLocalsAsync(ct);
        return JsonSerializer.Serialize(locals);
    }
    catch (Exception ex)
    {
        // Returns structured error — caller knows to wait for a stopped event first
        return JsonSerializer.Serialize(new { error = ex.Message });
    }
}
```

**Alternative:** Return events after continue operations. `debug_continue` can call
`ContinueAsync` then immediately call `WaitForEventAsync` and return the event
(StoppedEvent, BreakpointHitEvent, etc.) in the response. This gives Claude the
breakpoint hit signal in the same tool call.

### Anti-Patterns to Avoid

- **Writing to stdout from tools:** Breaks the MCP wire protocol. All Console.Write/
  Console.WriteLine must go to stderr or via ILogger.
- **Using `WithToolsFromAssembly()`:** Reflection-based scanning; prefer explicit
  `WithTools<DebuggerTools>()` for type safety and AOT compatibility.
- **Making DotnetDebugger transient or scoped:** It manages a single OS-level debug
  session with a dedicated thread — must be singleton.
- **Using static tool methods:** Static methods cannot receive constructor-injected
  singletons; use instance methods with constructor injection instead.
- **Returning void or Task without value:** The MCP protocol expects content blocks;
  always return `Task<string>` with a meaningful JSON payload.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| MCP JSON-RPC framing | Custom stdin/stdout reader | ModelContextProtocol SDK | Protocol has edge cases (chunked reads, content-length headers) |
| Tool parameter JSON schema | Manual schema construction | `[Description]` attributes | SDK generates schema from method parameters automatically |
| Service lifetime management | Manual instantiation | `AddSingleton<DotnetDebugger>()` | Host manages dispose; `IAsyncDisposable` called on shutdown |
| Tool return serialization | Custom ContentBlock construction | Return `Task<string>` with `JsonSerializer.Serialize` | SDK wraps string in TextContentBlock automatically |

---

## Complete DotnetDebugger API Surface for Tools

All signatures confirmed via roslyn-nav:

```csharp
// Launch & Attach
Task LaunchAsync(string projectPath, string appDllPath, CancellationToken ct = default)
Task AttachAsync(uint processId, CancellationToken ct = default)
Task DisconnectAsync(CancellationToken ct = default)

// Execution control
Task ContinueAsync(CancellationToken ct = default)
Task PauseAsync(CancellationToken ct = default)
Task StepOverAsync(CancellationToken ct = default)
Task StepIntoAsync(CancellationToken ct = default)
Task StepOutAsync(CancellationToken ct = default)

// Breakpoints
Task<int> SetBreakpointAsync(string dllPath, string sourceFile, int line, CancellationToken ct = default)
Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct = default)

// Inspection (require stopped state)
Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(CancellationToken ct = default)
Task<IReadOnlyList<VariableInfo>> GetLocalsAsync(CancellationToken ct = default)
Task<EvalResult> EvaluateAsync(string expression, CancellationToken ct = default)

// Events
Task<DebugEvent> WaitForEventAsync(CancellationToken ct = default)
```

**Note:** `LaunchAsync` takes both `projectPath` (for `dotnet build`) and `appDllPath`
(the compiled binary path). Both must be provided by the tool caller.

**Note:** `SetBreakpointAsync` returns `int` (breakpoint ID), not `BreakpointInfo`.

---

## Tool Return Format Design

All 14 tools should return `Task<string>` with JSON-serialized results. Structured
approach using anonymous records or dedicated result types:

```csharp
// Success pattern
return JsonSerializer.Serialize(new {
    success = true,
    data = result      // the actual return value
});

// Error pattern (catch block)
return JsonSerializer.Serialize(new {
    success = false,
    error = ex.Message
});
```

For the 14 tools:

| Tool | DotnetDebugger Call | Return Payload |
|------|---------------------|----------------|
| debug_launch | LaunchAsync | `{ status: "launched" }` |
| debug_attach | AttachAsync | `{ status: "attached", pid }` |
| debug_set_breakpoint | SetBreakpointAsync → int | `{ id: N, file, line }` |
| debug_remove_breakpoint | RemoveBreakpointAsync | `{ status: "removed", id }` |
| debug_continue | ContinueAsync + WaitForEventAsync | `{ event: { type, ... } }` |
| debug_step_over | StepOverAsync + WaitForEventAsync | `{ event: { type, ... } }` |
| debug_step_into | StepIntoAsync + WaitForEventAsync | `{ event: { type, ... } }` |
| debug_step_out | StepOutAsync + WaitForEventAsync | `{ event: { type, ... } }` |
| debug_variables | GetLocalsAsync | `[{ name, type, value, children }]` |
| debug_evaluate | EvaluateAsync | `{ success, value, error? }` |
| debug_stacktrace | GetStackTraceAsync | `[{ index, methodName, file?, line?, ilOffset }]` |
| debug_pause | PauseAsync + WaitForEventAsync | `{ event: { type, ... } }` |
| debug_disconnect | DisconnectAsync | `{ status: "disconnected" }` |
| debug_status | internal state field | `{ state: "idle|running|stopped|exited" }` |

**Key insight:** Execution-control tools (continue, step_over, step_into, step_out,
pause) should call `WaitForEventAsync` after the operation and include the resulting
`DebugEvent` in the response. This gives Claude feedback on what happened (hit breakpoint,
stepped, exited) in a single tool call instead of requiring a separate polling call.

---

## Common Pitfalls

### Pitfall 1: stdout Contamination
**What goes wrong:** Any `Console.Write` or `Console.WriteLine` call from either Program.cs
or DotnetDebugger breaks the JSON-RPC framing on stdout.
**Why it happens:** The MCP SDK reads stdout as a message stream; any non-JSON-RPC bytes
corrupt the protocol.
**How to avoid:** Add `builder.Logging.AddConsole(o => { o.LogToStandardErrorThreshold = LogLevel.Trace; })`
so all log output goes to stderr. Never call `Console.Write*` in MCP tool methods.
**Warning signs:** Client gets "unexpected end of JSON" or similar parse errors.

### Pitfall 2: Singleton vs. Scoped Lifetime Mismatch
**What goes wrong:** Registering `DotnetDebugger` as `AddScoped` creates a new instance
per MCP request, losing session state between calls.
**Why it happens:** The tool class is instantiated fresh per request if `DotnetDebugger`
has wrong lifetime.
**How to avoid:** Register as `AddSingleton<DotnetDebugger>()`. The host disposes it
on shutdown via `IAsyncDisposable`.
**Warning signs:** Second tool call fails because no process is attached.

### Pitfall 3: Calling Inspection Tools While Running
**What goes wrong:** `GetLocalsAsync`/`GetStackTraceAsync` internally call ICorDebug
APIs that HRESULT-fail if the process is running.
**Why it happens:** ICorDebug requires the process to be stopped (via CORDBG_S_AT_END_OF_STACK
or Stop() callback) before enumerating locals.
**How to avoid:** Catch exceptions in tool methods and return `{ error: "Process not
stopped. Call debug_continue and wait for a stopped event first." }`.
**Warning signs:** `COMException` with HRESULT 0x80131301 (CORDBG_E_PROCESS_NOT_SYNCHRONIZED).

### Pitfall 4: Tool Method Name vs. Required Tool Name Mismatch
**What goes wrong:** SDK auto-generates `get_variables` from method name `GetVariables`,
but Claude Code expects `debug_variables`.
**Why it happens:** Default snake_case conversion does not add the `debug_` prefix.
**How to avoid:** Always use `[McpServerTool(Name = "debug_xxx")]` explicitly on every
method.
**Warning signs:** Claude Code reports tool not found or calls wrong tool.

### Pitfall 5: WaitForEventAsync Deadlock in Single-Threaded Tool
**What goes wrong:** Tool calls `WaitForEventAsync` with no timeout and the process
never produces an event (e.g., process already exited).
**Why it happens:** `WaitForEventAsync` blocks on `Channel.Reader.ReadAsync` indefinitely.
**How to avoid:** Always pass `CancellationToken ct` (from MCP SDK) to `WaitForEventAsync`;
the MCP client will cancel when the request times out.
**Warning signs:** Tool hangs forever; MCP client connection drops.

---

## HelloDebug Status

`tests/HelloDebug/Program.cs` **already satisfies TEST-01** with 9 debug sections:

| Section | Variables Exposed | Debugger Feature |
|---------|-------------------|-----------------|
| Section 1 | `int counter`, `bool isActive`, `double ratio`, `char grade` | Primitives inspection |
| Section 2 | `string greeting`, `string multiWord`, `string? nullableStr` | String + nullable |
| Section 3 | Loop with `int i`, `counter`, `string step` | Mutation, watch |
| Section 4 | `List<int> numbers`, `Dictionary<string,int> lookup`, `int[] arr` | Collections |
| Section 5 | `Person person` with nested `Address Home` | Object graph, nested |
| Section 6 | Call to `Fibonacci(10)` | Step-into |
| Section 7 | `DivideByZeroException ex` caught | Exception handling |
| Section 8 | `await FetchValueAsync(7)` | Async method |
| Section 9 | `Node node` linked list (null-terminated) | Nested null objects |

The csproj has `<DebugType>portable</DebugType>` and `<Optimize>false</Optimize>` which
are required for PdbReader and variable visibility. **TEST-01 needs no changes.**

---

## Code Examples

### Program.cs (complete)
```csharp
// Source: https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

builder.Services.AddSingleton<DotnetDebugger>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<DebuggerTools>();

await builder.Build().RunAsync();
```

### DebuggerTools.cs skeleton (one tool per DotnetDebugger method)
```csharp
// Source: https://github.com/modelcontextprotocol/csharp-sdk
using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

[McpServerToolType]
public sealed class DebuggerTools(DotnetDebugger debugger)
{
    private string _state = "idle";   // idle | running | stopped | exited

    [McpServerTool(Name = "debug_launch"),
     Description("Build and launch a .NET project under the debugger. " +
                 "Returns when the process is created.")]
    public async Task<string> Launch(
        [Description("Path to the .csproj file or directory")] string projectPath,
        [Description("Path to the compiled .dll to debug")] string appDllPath,
        CancellationToken ct)
    {
        try
        {
            await debugger.LaunchAsync(projectPath, appDllPath, ct);
            _state = "running";
            return JsonSerializer.Serialize(new { success = true, state = _state });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "debug_continue"),
     Description("Resume execution. Waits for the next debug event (breakpoint hit, " +
                 "step complete, exception, or exit) and returns it.")]
    public async Task<string> Continue(CancellationToken ct)
    {
        try
        {
            await debugger.ContinueAsync(ct);
            _state = "running";
            var ev = await debugger.WaitForEventAsync(ct);
            _state = ev is ExitedEvent ? "exited" : "stopped";
            return JsonSerializer.Serialize(new { success = true, event = SerializeEvent(ev) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "debug_status"),
     Description("Returns the current debugger state: idle, running, stopped, or exited.")]
    public Task<string> GetStatus(CancellationToken ct)
    {
        return Task.FromResult(JsonSerializer.Serialize(new { state = _state }));
    }

    // ... 11 more tools following the same pattern
}
```

### csproj addition
```xml
<PackageReference Include="ModelContextProtocol" Version="0.9.0-preview.2" />
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `WithListToolsHandler` + `WithCallToolHandler` (low-level) | `[McpServerTool]` attribute + `WithTools<T>()` | 2024-Q4 | High-level; attributes generate schema automatically |
| `WithToolsFromAssembly()` reflection scan | `WithTools<T>()` explicit type | 2025 (AOT added) | Prefer explicit for AOT scenarios |
| `Task<CallToolResult>` return type | `Task<string>` return type | 2024 | SDK auto-wraps string in TextContentBlock |

---

## Open Questions

1. **State tracking location**
   - What we know: `DotnetDebugger` does not expose a `State` property; `_process` is
     private. State must be tracked externally.
   - What's unclear: Should state tracking live in `DebuggerTools` (simple field) or
     a shared `DebuggerSession` wrapper (better testability)?
   - Recommendation: Track `_state` string field in `DebuggerTools` — simple, works
     for Phase 4. Phase 5 tests can add a session wrapper if needed.

2. **debug_continue event delivery**
   - What we know: `WaitForEventAsync` blocks until next event; execution-control tools
     call it after the operation.
   - What's unclear: If multiple events fire quickly (e.g., module load + breakpoint hit),
     `WaitForEventAsync` returns the first one and subsequent events are lost until the
     next tool call.
   - Recommendation: For Phase 4, return only the next event. If filtering is needed
     (e.g., skip OutputEvent), add a loop that reads events until a "significant" one
     (StoppedEvent, BreakpointHitEvent, ExceptionEvent, ExitedEvent).

3. **debug_launch parameter UX**
   - What we know: `LaunchAsync` requires both `projectPath` AND `appDllPath` separately.
   - What's unclear: Should the tool accept only `projectPath` and derive `appDllPath`
     automatically (e.g., from `dotnet build` output)?
   - Recommendation: Accept `projectPath` only and derive `appDllPath` as
     `{projectPath}/bin/Debug/net10.0/{AssemblyName}.dll` via convention, or make
     `appDllPath` optional with auto-detection. This makes the tool easier to use from
     Claude Code.

---

## Sources

### Primary (HIGH confidence)
- [ModelContextProtocol NuGet 0.9.0-preview.2](https://www.nuget.org/packages/ModelContextProtocol/) — version, dependencies
- [.NET Blog: Build MCP server in C#](https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/) — Program.cs pattern, WithStdioServerTransport
- [csharp-sdk GitHub README](https://github.com/modelcontextprotocol/csharp-sdk) — WithTools<T>, McpServerTool attribute API
- [DeepWiki: Server Tools](https://deepwiki.com/modelcontextprotocol/csharp-sdk/2.1-server-tools) — return type table, naming convention, DI parameter injection rules
- roslyn-nav: DotnetDebugger member inspection — all 14 method signatures confirmed

### Secondary (MEDIUM confidence)
- [WeatherTools sample](https://github.com/modelcontextprotocol/csharp-sdk/blob/main/samples/QuickstartWeatherServer) — non-static class pattern, constructor injection, HttpClient DI
- [McpServerToolAttribute API docs](https://modelcontextprotocol.github.io/csharp-sdk/api/ModelContextProtocol.Server.McpServerToolAttribute.html) — Name property, Description

### Tertiary (LOW confidence)
- N/A — no unverified claims made

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — version confirmed on nuget.org 2026-02-21
- Architecture: HIGH — Program.cs pattern and tool attributes verified against official docs and samples
- DotnetDebugger API: HIGH — confirmed via roslyn-nav on actual source code
- Pitfalls: MEDIUM — stdout contamination and singleton lifetime from verified patterns; inspection-while-running from known ICorDebug behavior
- HelloDebug status: HIGH — file read directly from disk

**Research date:** 2026-02-22
**Valid until:** 2026-03-22 (prerelease SDK — check for 0.9.x updates before planning)
