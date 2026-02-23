# Phase 9: Tests & Documentation - Research

**Researched:** 2026-02-23
**Domain:** xUnit integration testing of ICorDebug debugger + README documentation
**Confidence:** HIGH

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| TEST-02 | PdbReaderTests.cs — tests forward and reverse PDB lookup for HelloDebug | PdbReader has static methods `FindLocation` and `ReverseLookup` — straightforward unit tests using the HelloDebug.dll artifact from its Debug build |
| TEST-03 | DebuggerIntegrationTests.cs — full flow: launch → breakpoint → variables → continue → exit, no manual intervention | DotnetDebugger is the system under test; IClassFixture with IAsyncLifetime provides a single debugger instance; tests run sequentially via [Collection] |
| TEST-09 | Integration tests for unhandled exceptions, multi-thread inspection, and process attach | Three separate Fact methods using the same fixture; HelloDebug already has sections 20 (threading) and 21 (unhandled exception); attach requires a separately launched process |
| DOCS-01 | README rewrite: ASCII architecture diagram, prerequisites, libdbgshim.so location, build.sh/install.sh steps, usage examples for all 14+1 tools | README is completely stale (Python/netcoredbg era); current architecture well-understood from codebase; libdbgshim.so location issue found (see Pitfall 3) |
</phase_requirements>

---

## Summary

Phase 9 has two parallel workstreams: (1) automated xUnit tests covering PdbReader and full debugger integration scenarios, and (2) a complete README rewrite reflecting the current C#/ICorDebug architecture.

For the test workstream, the central challenge is that `DotnetDebugger` is a stateful singleton that owns a dedicated ICorDebug dispatch thread. Integration tests must run sequentially against a shared fixture — not in parallel — because the debugger can only manage one debug session at a time. The `IAsyncLifetime` interface on a `ICollectionFixture` is the correct xUnit pattern: it gives async `InitializeAsync`/`DisposeAsync` with session-level lifetime across all test classes. Each `[Fact]` test drives a complete debug scenario (launch → breakpoint → inspect → continue → exit) with explicit timeouts via `CancellationTokenSource`. The `WaitForEventAsync` polling pattern from the MCP tools maps directly to test assertions.

For the documentation workstream, the README.md currently describes the old Python/netcoredbg architecture — it must be completely replaced. The new README needs: (a) an ASCII architecture diagram showing Claude Code → MCP Server (stdio) → DotnetDebugger → ICorDebug → libdbgshim.so → .NET Process, (b) correct prerequisites including `DBGSHIM_PATH` env var or `~/.local/bin/libdbgshim.so` install, (c) build.sh and install.sh usage, and (d) usage examples for all 15 tools (14 original + debug_launch_test from Phase 8).

**Primary recommendation:** Use `ICollectionFixture<DebuggerFixture>` with `IAsyncLifetime` and `[Collection("Debugger")]` on all integration test classes; run each scenario as a full session lifecycle; use `CancellationTokenSource(TimeSpan.FromSeconds(30))` timeouts on all `WaitForEventAsync` calls.

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| xunit | 2.9.3 | Test framework — already in Tests.csproj | Already installed, no migration needed |
| xunit.runner.visualstudio | 3.1.4 | Test runner integration — already in Tests.csproj | Already installed |
| Microsoft.NET.Test.Sdk | 17.14.1 | `dotnet test` runner infrastructure — already in Tests.csproj | Already installed |

### No new NuGet packages needed
All required libraries are already referenced in `tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj`. The project already references `DebuggerNetMcp.Core` as a ProjectReference.

### xUnit v2 vs v3 Decision
The project currently uses **xUnit v2 (2.9.3)**. Do NOT migrate to v3 for this phase. v3 would require changing `OutputType` to `Exe` and restructuring imports — unnecessary churn. `IAsyncLifetime` with `InitializeAsync`/`DisposeAsync` returning `Task` works correctly in v2.9.3. v3's `IAsyncLifetime` changed to return `ValueTask`; the v2 version is sufficient.

### csproj additions needed
The Tests.csproj needs debug symbol/optimization settings to match HelloDebug, so PdbReaderTests can find sequence points. Also needs:
```xml
<!-- Required for PdbReaderTests to locate HelloDebug build artifacts -->
<PropertyGroup>
  <DebugType>portable</DebugType>
</PropertyGroup>
```

---

## Architecture Patterns

### Pattern 1: ICollectionFixture with IAsyncLifetime for Sequential Integration Tests

**What:** A single `DebuggerFixture` class implements `IAsyncLifetime` and creates one `DotnetDebugger` instance. All test classes use `[Collection("Debugger")]` to share this fixture and run sequentially (tests in the same collection never run in parallel).

**When to use:** Any test that uses `DotnetDebugger` — the debugger is not thread-safe across sessions and must be used sequentially.

**Implementation:**
```csharp
// DebuggerFixture.cs
public sealed class DebuggerFixture : IAsyncLifetime
{
    public DotnetDebugger Debugger { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // Pass explicit path so tests don't depend on env var
        Debugger = new DotnetDebugger(
            dbgShimPath: Environment.GetEnvironmentVariable("DBGSHIM_PATH")
                         ?? Path.Combine(Environment.GetFolderPath(
                              Environment.SpecialFolder.UserProfile),
                              ".local", "bin", "libdbgshim.so"));
    }

    public async Task DisposeAsync()
    {
        await Debugger.DisposeAsync();
    }
}

[CollectionDefinition("Debugger", DisableParallelization = true)]
public class DebuggerCollection : ICollectionFixture<DebuggerFixture> { }
```

**Key constraint:** `DisableParallelization = true` ensures tests run one at a time. Without this, two tests could attempt simultaneous debug sessions on the same `DotnetDebugger`.

### Pattern 2: Per-Test Session Lifecycle

**What:** Each `[Fact]` runs a complete independent debug session: launch → set breakpoints → continue → assert events/variables → verify exit. `DisconnectAsync` at the start of `LaunchAsync` (already implemented) clears prior session state.

**When to use:** All `DebuggerIntegrationTests` facts.

**Implementation:**
```csharp
[Collection("Debugger")]
public class DebuggerIntegrationTests(DebuggerFixture fixture)
{
    private DotnetDebugger Dbg => fixture.Debugger;
    private const string HelloDebugProject = "../../../HelloDebug"; // relative to test bin
    private const string HelloDebugDll = "../../../HelloDebug/bin/Debug/net10.0/HelloDebug.dll";

    [Fact]
    public async Task Launch_SetBreakpoint_HitsAndInspectsVariable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);
        // LaunchAsync returns after CreateProcess (state=stopped)

        int bpId = await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 17, cts.Token);
        // Line 17: "int counter = 0;"  (Section 1 BP-1)

        await Dbg.ContinueAsync(cts.Token);
        var ev = await Dbg.WaitForEventAsync(cts.Token);
        var hit = Assert.IsType<BreakpointHitEvent>(ev);
        Assert.Equal(bpId, hit.BreakpointId);

        var vars = await Dbg.GetLocalsAsync(0, cts.Token);
        var counter = vars.First(v => v.Name == "counter");
        Assert.Equal("0", counter.Value);

        await Dbg.ContinueAsync(cts.Token);
        var exitEv = await Dbg.WaitForEventAsync(cts.Token);
        // May need additional continues past sections...
    }
}
```

### Pattern 3: PdbReaderTests as Pure Unit Tests

**What:** PdbReader methods are all `static` — no DotnetDebugger needed. Tests just need the compiled HelloDebug.dll path.

**When to use:** TEST-02 (PdbReaderTests.cs).

**Implementation:**
```csharp
public class PdbReaderTests
{
    // HelloDebug is built as Debug by default (DebugType=portable in its .csproj)
    private static readonly string HelloDebugDll =
        Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",  // up from bin/Debug/net10.0
            "HelloDebug", "bin", "Debug", "net10.0", "HelloDebug.dll"));

    [Fact]
    public void FindLocation_Section1Primitives_ReturnsValidToken()
    {
        // Program.cs line 17 = "int counter = 0;"  in Section 1
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 17);
        Assert.NotEqual(0, methodToken);
        Assert.True(ilOffset >= 0);
    }

    [Fact]
    public void ReverseLookup_KnownToken_ReturnsSourceLine()
    {
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 17);
        var result = PdbReader.ReverseLookup(HelloDebugDll, methodToken, ilOffset);
        Assert.NotNull(result);
        Assert.Contains("Program.cs", result.Value.sourceFile, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(17, result.Value.line);
    }
}
```

### Pattern 4: WaitForEvent with Timeout and Event Draining

**What:** After `ContinueAsync`, the debuggee may emit `OutputEvent` entries before hitting a breakpoint. Use a loop to drain non-halting events.

**When to use:** Any test that needs to reach a specific event type (BreakpointHitEvent, ExceptionEvent, ExitedEvent).

**Implementation:**
```csharp
private static async Task<T> WaitForSpecificEvent<T>(
    DotnetDebugger dbg, CancellationToken ct) where T : DebugEvent
{
    while (true)
    {
        var ev = await dbg.WaitForEventAsync(ct);
        if (ev is T typedEv) return typedEv;
        if (ev is ExitedEvent) throw new Exception($"Process exited before {typeof(T).Name}");
        // OutputEvent, StoppedEvent for non-target stops — continue draining
    }
}
```

### Recommended Test Structure
```
tests/DebuggerNetMcp.Tests/
├── DebuggerNetMcp.Tests.csproj       # already exists — no NuGet changes needed
├── UnitTest1.cs                      # rename/replace: MathTests (DTEST fixture tests)
├── PdbReaderTests.cs                 # TEST-02: static PdbReader unit tests
├── DebuggerFixture.cs                # shared fixture + CollectionDefinition
├── DebuggerIntegrationTests.cs       # TEST-03: launch/breakpoint/variables/step/exit
└── DebuggerAdvancedTests.cs          # TEST-09: exceptions, multi-thread, attach
```

### Anti-Patterns to Avoid
- **Parallel execution across debugger tests:** Never remove `DisableParallelization = true` or use different collection names for tests sharing `DotnetDebugger`. ICorDebug is not safe for concurrent sessions.
- **Hardcoded absolute paths:** Use `AppContext.BaseDirectory` + relative path arithmetic, not `/home/user/...` paths. The test runner executes from `bin/Debug/net10.0/`.
- **Missing CancellationToken on all WaitForEventAsync calls:** Without a timeout, a hung debuggee blocks the entire test run forever.
- **Testing against Release builds of HelloDebug:** Optimized builds collapse variables — always use Debug build (`-c Debug`) for integration tests.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Sequential test execution | `lock`, `SemaphoreSlim` between tests | `[Collection("Debugger")]` with `DisableParallelization = true` | xUnit collection fixtures guarantee sequential execution within a collection |
| Async fixture setup/teardown | Thread.Sleep or synchronous constructor | `IAsyncLifetime.InitializeAsync/DisposeAsync` | xUnit v2 supports this natively; constructors cannot be async |
| Event draining loop | Custom `Channel<T>` wrapper | Simple `while (true)` loop over `WaitForEventAsync` | `DotnetDebugger.WaitForEventAsync` already exposes the event channel |
| Test timeout | Manual `Task.Delay` + `Task.WhenAny` | `CancellationTokenSource(TimeSpan)` passed to `WaitForEventAsync` | All async DotnetDebugger methods accept `CancellationToken` |

**Key insight:** The `DotnetDebugger` public API was designed to be testable — all methods are async with `CancellationToken`. The integration tests are essentially scripted debug sessions, not framework abstractions.

---

## Common Pitfalls

### Pitfall 1: HelloDebug Must Be Built Before Tests Run
**What goes wrong:** `PdbReaderTests` and integration tests reference HelloDebug's compiled DLL. If HelloDebug hasn't been built, tests fail with `FileNotFoundException` on the DLL path.
**Why it happens:** `dotnet test` builds the test project but NOT referenced DLLs from sibling projects.
**How to avoid:** Add a `dotnet build ../HelloDebug -c Debug` to either the fixture's `InitializeAsync` or a test setup method. Alternatively, add HelloDebug as a `ProjectReference` with `ReferenceOutputAssembly=false` to trigger an implicit build.
**Recommended approach:**
```xml
<!-- In DebuggerNetMcp.Tests.csproj -->
<ItemGroup>
  <!-- Builds HelloDebug before tests run but doesn't reference its assembly -->
  <ProjectReference Include="..\..\tests\HelloDebug\HelloDebug.csproj"
                    ReferenceOutputAssembly="false"
                    Private="false" />
</ItemGroup>
```
**Warning signs:** `Could not find file 'HelloDebug/bin/Debug/net10.0/HelloDebug.dll'` errors.

### Pitfall 2: DLL Path Resolution from Test Runner's BaseDirectory
**What goes wrong:** `AppContext.BaseDirectory` is `tests/DebuggerNetMcp.Tests/bin/Debug/net10.0/`. Navigating to HelloDebug from there requires `../../../HelloDebug/bin/Debug/net10.0/HelloDebug.dll` (three levels up from the test bin dir, then into HelloDebug). Getting this wrong causes FileNotFound.
**Why it happens:** The test runner CWD is not the solution root.
**How to avoid:** Use `Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HelloDebug", ...))`. Verify with a `File.Exists` assertion as the first test step.

### Pitfall 3: libdbgshim.so Not Found in Default Search Path
**What goes wrong:** `DotnetDebugger` constructor calls `DbgShimInterop.Load()`. The search path does NOT include `~/.local/bin/`. The current dev setup puts `libdbgshim.so` there, but the `BuildCandidateList` looks in: explicit path → `DBGSHIM_PATH` env var → `DOTNET_ROOT/shared/...` runtime dirs → `/usr/share/dotnet/...` → `~/.local/lib/netcoredbg/` → `/usr/local/lib/netcoredbg/`.
**Why it happens:** `~/.local/bin/libdbgshim.so` is not in any of the 7 search paths.
**How to avoid in tests:** Set `DBGSHIM_PATH` env var OR pass explicit path in `DebuggerFixture.InitializeAsync`: `new DotnetDebugger(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", "libdbgshim.so"))`.
**Impact on README:** README must document the correct `DBGSHIM_PATH` env var usage AND note that `~/.local/bin/` is a valid location but requires the env var.

### Pitfall 4: Integration Tests Emit Multiple Events Between Breakpoints
**What goes wrong:** `WaitForEventAsync` returns the next event on the channel — which might be `OutputEvent` (console output) or `StoppedEvent` (step complete) before the expected `BreakpointHitEvent`.
**Why it happens:** HelloDebug is a chatty program; every `Console.WriteLine` emits `OutputEvent`. After `ContinueAsync`, many events may arrive before the target breakpoint.
**How to avoid:** Use the `WaitForSpecificEvent<T>` drain-loop pattern (Pattern 4 above). Never assert `Assert.IsType<BreakpointHitEvent>` on the immediate next `WaitForEventAsync` without draining.

### Pitfall 5: Section 21 (Unhandled Exception) Terminates the Process
**What goes wrong:** HelloDebug's section 21 throws `InvalidOperationException("Section 21 unhandled")` — this terminates the process. If the exception test doesn't properly handle the `ExceptionEvent` + subsequent `ExitedEvent`, the session is left in a dirty state for the next test.
**Why it happens:** The unhandled exception fires an `ExceptionEvent` (IsUnhandled=true), followed by an `ExitedEvent`. Both must be consumed before `DisconnectAsync`.
**How to avoid:** After the exception test, always call `DisconnectAsync` to reset state before the next test scenario. The fixture's `DisposeAsync` does this automatically after all tests, but individual tests must not share session state.

### Pitfall 6: Process Attach Tests Require a Living Target Process
**What goes wrong:** TEST-09 includes a process attach scenario (`ATCH-01`). The test must launch a .NET process separately, then call `AttachAsync(pid)`. If the process exits before attach, `AttachAsync` fails.
**Why it happens:** Race condition between process launch and attach.
**How to avoid:** Launch a helper process that stays alive (e.g., a simple `Thread.Sleep(60_000)` loop) before calling `AttachAsync`. Use a `Process.Start` with `UseShellExecute=false` and capture the PID from `process.Id`.

---

## Code Examples

### PdbReader Forward Lookup Test

```csharp
// Source: PdbReader.FindLocation signature from roslyn-nav inspection
[Fact]
public void FindLocation_KnownSourceLine_ReturnsNonZeroToken()
{
    // Program.cs:17 = "int counter = 0;" in HelloDebug Section 1
    var (methodToken, ilOffset) = PdbReader.FindLocation(
        HelloDebugDll, "Program.cs", 17);

    Assert.NotEqual(0, methodToken);
    Assert.True(ilOffset >= 0);
}
```

### PdbReader Reverse Lookup Round-Trip Test

```csharp
// Source: PdbReader.ReverseLookup signature from roslyn-nav inspection
[Fact]
public void ReverseLookup_ForwardResult_RoundTrips()
{
    var (methodToken, ilOffset) = PdbReader.FindLocation(
        HelloDebugDll, "Program.cs", 17);
    Assert.NotEqual(0, methodToken);

    var result = PdbReader.ReverseLookup(HelloDebugDll, methodToken, ilOffset);

    Assert.NotNull(result);
    Assert.Contains("Program.cs", result.Value.sourceFile, StringComparison.OrdinalIgnoreCase);
    Assert.Equal(17, result.Value.line);
}
```

### Integration Test: Launch + Breakpoint + Variables

```csharp
// Source: DotnetDebugger public API from roslyn-nav inspection
[Fact]
public async Task LaunchAsync_SetBreakpoint_InspectsLocals()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);
    // State = stopped (CreateProcess callback)

    int bpId = await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 17, cts.Token);

    // Continue past CreateProcess; loop until BreakpointHitEvent
    await Dbg.ContinueAsync(cts.Token);
    var hit = await WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);
    Assert.Equal(bpId, hit.BreakpointId);

    var vars = await Dbg.GetLocalsAsync(0, cts.Token);
    Assert.Contains(vars, v => v.Name == "counter" && v.Value == "0");

    // Let process run to natural exit (section 21 throws)
    await Dbg.ContinueAsync(cts.Token);
    // drain remaining events until ExitedEvent
    await DrainToExit(Dbg, cts.Token);
    await Dbg.DisconnectAsync(cts.Token);
}
```

### Integration Test: Unhandled Exception (TEST-09)

```csharp
[Fact]
public async Task Launch_UnhandledException_DeliversExceptionEvent()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);
    // No breakpoints — let it run straight to section 21 unhandled throw
    await Dbg.ContinueAsync(cts.Token);

    var exEv = await WaitForSpecificEvent<ExceptionEvent>(Dbg, cts.Token);
    Assert.True(exEv.IsUnhandled);
    Assert.Contains("InvalidOperationException", exEv.ExceptionType);
    Assert.Contains("Section 21 unhandled", exEv.Message);

    // Process exits after unhandled exception
    await DrainToExit(Dbg, cts.Token);
    await Dbg.DisconnectAsync(cts.Token);
}
```

### Integration Test: Process Attach (TEST-09)

```csharp
[Fact]
public async Task AttachAsync_RunningProcess_AttachesSuccessfully()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

    // Launch a target process that stays alive
    using var target = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
    {
        FileName = "dotnet",
        Arguments = HelloDebugDll,
        UseShellExecute = false,
        RedirectStandardOutput = true,
    })!;

    // Give it a moment to initialize the CLR
    await Task.Delay(500, cts.Token);

    var (pid, name) = await Dbg.AttachAsync((uint)target.Id, cts.Token);
    Assert.Equal((uint)target.Id, pid);
    Assert.NotEmpty(name);

    await Dbg.DisconnectAsync(cts.Token);
    target.Kill();
}
```

### README Architecture Diagram (ASCII)

```
┌─────────────────────────────────────────────────────────────────┐
│                        Claude Code (LLM)                         │
└───────────────────────────┬─────────────────────────────────────┘
                            │ MCP stdio (JSON-RPC)
┌───────────────────────────▼─────────────────────────────────────┐
│               DebuggerNetMcp.Mcp (MCP Server)                    │
│  DebuggerTools.cs — 15 [McpServerTool] methods                   │
└───────────────────────────┬─────────────────────────────────────┘
                            │ C# method calls
┌───────────────────────────▼─────────────────────────────────────┐
│             DebuggerNetMcp.Core (Debug Engine)                   │
│  DotnetDebugger.cs — ICorDebug dispatch thread + event channel   │
│  PdbReader.cs      — Portable PDB source mapping                 │
│  VariableReader.cs — ICorDebugValue recursive inspection         │
└───────────┬──────────────────────────────────────────┬──────────┘
            │ P/Invoke                                  │ COM
┌───────────▼──────────┐                  ┌────────────▼──────────┐
│  libdbgshim.so       │                  │  ICorDebug (COM)       │
│  (DbgShim 10.0.14)   │                  │  ManagedCallbackHandler│
└──────────────────────┘                  └────────────┬──────────┘
                                                       │ ICorDebug API
                                          ┌────────────▼──────────┐
                                          │   .NET Process         │
                                          │   (debuggee)           │
                                          └───────────────────────┘
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Python/asyncio + netcoredbg + DAP | C#/ICorDebug direct (no DAP layer) | Phase 1-4 (2026-02-22) | Eliminates netcoredbg race condition at root |
| README documents Python setup | README must document C# build/install | Phase 9 (this phase) | README is 100% stale — complete rewrite needed |
| MathTests in UnitTest1.cs | PdbReaderTests + DebuggerIntegrationTests + DebuggerAdvancedTests | Phase 9 (this phase) | UnitTest1.cs should be renamed/repurposed |

**Deprecated in README:**
- All Python/uv references — eliminated in Phase 1
- netcoredbg references — eliminated in Phase 1
- DAP protocol documentation — eliminated in Phase 1
- `NETCOREDBG_PATH` env var — still searched by `BuildCandidateList` (backward compat) but no longer needed
- Old `claude mcp add` command with `uv --directory ... run python -m debugger_net_mcp`

---

## Open Questions

1. **Should UnitTest1.cs be renamed or replaced?**
   - What we know: `UnitTest1.cs` contains `MathTests` (the DTEST verification tests). These are useful for manual `debug_launch_test` verification but aren't automated integration tests.
   - What's unclear: Should MathTests stay as-is in UnitTest1.cs (for debug_launch_test manual testing), or be merged into DebuggerIntegrationTests, or kept separate?
   - Recommendation: Keep MathTests in UnitTest1.cs — they serve a different purpose (testing the xUnit-debugging workflow). Rename the file to `MathTests.cs` for clarity.

2. **How many breakpoint scenarios to cover in TEST-03?**
   - What we know: TEST-03 requires "launch, breakpoint, variables, step, and exit" coverage.
   - What's unclear: Whether to test all 21 sections of HelloDebug or just representative scenarios.
   - Recommendation: Cover 3-4 representative scenarios: (a) primitive variables, (b) step-over/step-into, (c) collections/objects. Don't try to automate all 21 sections — flakiness risk outweighs coverage gain.

3. **Multi-thread test: HelloDebug section 20 requires race-condition timing**
   - What we know: Section 20 starts a background thread and immediately `Thread.Sleep(50)` to give it time to reach BP-20. In automated tests, timing is less predictable.
   - What's unclear: Whether the 50ms delay in HelloDebug is reliable under `dotnet test` load.
   - Recommendation: Test multi-thread inspection by using `GetAllThreadStackTracesAsync` (already exists) to verify multiple threads are visible, rather than setting a breakpoint inside the background thread. This avoids the race.

4. **DBGSHIM_PATH for CI environments**
   - What we know: `~/.local/bin/libdbgshim.so` is not in the search path; tests need the path explicitly or via env var.
   - What's unclear: How future CI will supply the path.
   - Recommendation: In `DebuggerFixture`, check `DBGSHIM_PATH` env var first, then fall back to `~/.local/bin/libdbgshim.so`. Document in README that this env var must be set.

---

## Sources

### Primary (HIGH confidence)
- Codebase inspection via roslyn-nav — DotnetDebugger.cs public API, PdbReader.cs methods, DbgShimInterop.cs search paths, DebuggerTools.cs tool list
- `tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj` — confirmed xunit 2.9.3, Microsoft.NET.Test.Sdk 17.14.1 already present
- `tests/HelloDebug/Program.cs` — all 21 sections confirmed, Section 21 terminates via unhandled exception
- [xUnit.net Shared Context docs](https://xunit.net/docs/shared-context) — IClassFixture, ICollectionFixture, IAsyncLifetime patterns verified

### Secondary (MEDIUM confidence)
- [xUnit IAsyncLifetime blog — 2017 (still accurate for v2)](https://mderriey.com/2017/09/04/async-lifetime-with-xunit/) — InitializeAsync/DisposeAsync lifecycle verified against current official docs
- [xUnit parallel test docs](https://xunit.net/docs/running-tests-in-parallel) — DisableParallelization attribute and Collection behavior verified

### Tertiary (LOW confidence — for awareness only)
- xUnit v3 migration info (2025) — noted that v3 changes IAsyncLifetime to ValueTask; **not applicable** since project uses v2.9.3

---

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — existing csproj already has all needed packages; confirmed via file read
- Architecture (test patterns): HIGH — xUnit v2 fixture/collection patterns verified against official docs; DotnetDebugger API confirmed via roslyn-nav
- Pitfalls: HIGH — Pitfall 3 (libdbgshim search path gap) discovered via direct code inspection; others from direct codebase analysis
- README content: HIGH — current README is stale Python content confirmed by file read; architecture well-understood from codebase

**Research date:** 2026-02-23
**Valid until:** 2026-03-25 (stable — xUnit 2.9.x is in maintenance mode, no breaking changes expected)
