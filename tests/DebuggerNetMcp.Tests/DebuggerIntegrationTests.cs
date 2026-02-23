using System.Diagnostics;
using DebuggerNetMcp.Core.Engine;

namespace DebuggerNetMcp.Tests;

[Collection("Debugger")]
public class DebuggerIntegrationTests(DebuggerFixture fixture)
{
    private DotnetDebugger Dbg => fixture.Debugger;

    // HelloDebug project root (for dotnet build inside LaunchAsync)
    private static readonly string HelloDebugProject = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HelloDebug"));

    // HelloDebug compiled DLL (Debug build)
    private static readonly string HelloDebugDll = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "HelloDebug", "bin", "Debug", "net10.0", "HelloDebug.dll"));

    // ─── Helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Drains events until the requested type arrives. Throws if process exits first.
    /// </summary>
    private static async Task<T> WaitForSpecificEvent<T>(
        DotnetDebugger dbg, CancellationToken ct) where T : DebugEvent
    {
        while (true)
        {
            var ev = await dbg.WaitForEventAsync(ct);
            if (ev is T typedEv) return typedEv;
            if (ev is ExitedEvent) throw new Exception($"Process exited before {typeof(T).Name}");
            // OutputEvent, StoppedEvent — keep draining
        }
    }

    /// <summary>
    /// Drains all events (continuing on breakpoint/stopped/exception) until ExitedEvent.
    /// HelloDebug Section 21 throws an unhandled exception which stops the process;
    /// we must call ContinueAsync on ExceptionEvent so the process proceeds to exit.
    /// </summary>
    private static async Task DrainToExit(DotnetDebugger dbg, CancellationToken ct)
    {
        while (true)
        {
            var ev = await dbg.WaitForEventAsync(ct);
            if (ev is ExitedEvent) return;
            if (ev is BreakpointHitEvent or StoppedEvent or ExceptionEvent)
                await dbg.ContinueAsync(ct); // keep process running toward exit
        }
    }

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task LaunchAsync_SetBreakpoint_HitsAndInspectsVariable()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // Set breakpoint on Section 1: int counter = 0; (line 17)
        int bpId = await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 17, cts.Token);

        // Continue past CreateProcess stop
        await Dbg.ContinueAsync(cts.Token);

        // Wait for our breakpoint
        var hit = await WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);
        Assert.Equal(bpId, hit.BreakpointId);

        // Inspect locals — counter should be visible with value "0"
        var locals = await Dbg.GetLocalsAsync(0, cts.Token);
        var counterVar = locals.FirstOrDefault(v => v.Name == "counter");
        Assert.NotNull(counterVar);
        Assert.Equal("0", counterVar.Value);

        // Let the process finish
        await Dbg.ContinueAsync(cts.Token);
        await DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task LaunchAsync_StepOver_AdvancesLine()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // Set breakpoint on line 17 (int counter = 0;)
        await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 17, cts.Token);

        // Continue past CreateProcess stop
        await Dbg.ContinueAsync(cts.Token);

        // Wait for breakpoint hit
        await WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);

        // Step over — advances to next line
        await Dbg.StepOverAsync(cts.Token);
        await WaitForSpecificEvent<StoppedEvent>(Dbg, cts.Token);

        // counter variable should still be visible after the step
        var locals = await Dbg.GetLocalsAsync(0, cts.Token);
        Assert.Contains(locals, v => v.Name == "counter");

        await Dbg.ContinueAsync(cts.Token);
        await DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task LaunchAsync_StepInto_EntersMethod()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // Set breakpoint on Section 6: int fib = Fibonacci(10); (line 58)
        await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 58, cts.Token);

        // Continue past CreateProcess stop
        await Dbg.ContinueAsync(cts.Token);

        // Wait for breakpoint hit on Fibonacci call
        await WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);

        // Step into Fibonacci()
        await Dbg.StepIntoAsync(cts.Token);
        await WaitForSpecificEvent<StoppedEvent>(Dbg, cts.Token);

        // Should now be inside Fibonacci — stack must have at least 2 frames
        var frames = await Dbg.GetStackTraceAsync(0, cts.Token);
        Assert.True(frames.Count >= 2, $"Expected >= 2 frames after StepInto, got {frames.Count}");

        await Dbg.ContinueAsync(cts.Token);
        await DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task LaunchAsync_NaturalExit_DeliversExitedEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // No breakpoints — just let the process run to its unhandled exception / exit
        await Dbg.ContinueAsync(cts.Token);

        // DrainToExit will consume OutputEvents and stop when ExitedEvent arrives
        await DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }
}
