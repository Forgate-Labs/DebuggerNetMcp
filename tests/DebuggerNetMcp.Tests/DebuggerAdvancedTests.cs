using System.Diagnostics;
using DebuggerNetMcp.Core.Engine;

namespace DebuggerNetMcp.Tests;

[Collection("Debugger")]
public class DebuggerAdvancedTests(DebuggerFixture fixture)
{
    private DotnetDebugger Dbg => fixture.Debugger;

    // HelloDebug project root (for dotnet build inside LaunchAsync)
    private static readonly string HelloDebugProject = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "HelloDebug"));

    // HelloDebug compiled DLL (Debug build)
    private static readonly string HelloDebugDll = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "HelloDebug", "bin", "Debug", "net10.0", "HelloDebug.dll"));

    // ─── Tests ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Launch_UnhandledException_DeliversExceptionEvent()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // No breakpoints — run until Section 21 throws unhandled InvalidOperationException
        await Dbg.ContinueAsync(cts.Token);

        // Wait for the unhandled exception event
        var exEv = await DebuggerTestHelpers.WaitForSpecificEvent<ExceptionEvent>(Dbg, cts.Token);

        Assert.True(exEv.IsUnhandled, "Expected IsUnhandled == true for Section 21 exception");
        Assert.Contains("InvalidOperationException", exEv.ExceptionType);
        Assert.Contains("Section 21 unhandled", exEv.Message);

        // Continue once more so the process can exit after the unhandled exception
        await Dbg.ContinueAsync(cts.Token);

        // Drain remaining events to ExitedEvent
        await DebuggerTestHelpers.DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task Launch_MultipleThreads_AllThreadsVisible()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);

        // Set BP on Section 20 background thread WriteLine BEFORE continue (line 149)
        // Process is stopped at CreateProcess so modules aren't loaded yet —
        // the breakpoint will be pending until the first ContinueAsync triggers LoadModule.
        await Dbg.SetBreakpointAsync(HelloDebugDll, "Program.cs", 149, cts.Token);

        // Continue past CreateProcess stop — pending BP activates on module load
        await Dbg.ContinueAsync(cts.Token);

        // Wait for the breakpoint inside the background thread
        await DebuggerTestHelpers.WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);

        // Both main thread and background thread should be visible
        var allThreads = await Dbg.GetAllThreadStackTracesAsync(cts.Token);
        Assert.True(allThreads.Count >= 2,
            $"Expected >= 2 threads at BP-20, got {allThreads.Count}");

        await Dbg.ContinueAsync(cts.Token);
        await DebuggerTestHelpers.DrainToExit(Dbg, cts.Token);

        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task PauseAsync_SuspendsAllThreads_NoEventAfterPause()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);
        await Dbg.ContinueAsync(cts.Token); // let process run past CreateProcess stop

        await Task.Delay(50, cts.Token);   // give it time to be "running"
        await Dbg.PauseAsync(cts.Token);   // ICorDebug Stop(0) — synchronous suspend all managed threads

        // After pause: process is stopped; inspection APIs must work without errors
        var allThreads = await Dbg.GetAllThreadStackTracesAsync(cts.Token);
        Assert.NotEmpty(allThreads); // at least main thread visible

        // Resume and drain to exit
        await Dbg.ContinueAsync(cts.Token);
        await DebuggerTestHelpers.DrainToExit(Dbg, cts.Token);
        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task LaunchTestAsync_BreakpointInFact_HitsWithLocals()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        // The test project is the same project running this test
        // AppContext.BaseDirectory = tests/DebuggerNetMcp.Tests/bin/Debug/net10.0/
        // 3 levels up reaches tests/DebuggerNetMcp.Tests/ (the project directory)
        var testProject = Path.GetFullPath(
            Path.Combine(AppContext.BaseDirectory, "..", "..", ".."));

        // The test assembly DLL is already compiled in the test output directory
        var testsDll = Path.Combine(AppContext.BaseDirectory, "DebuggerNetMcp.Tests.dll");

        // Filter: only run AddTwoNumbers_ReturnsCorrectSum — CRITICAL to prevent recursive test spawning
        await Dbg.LaunchTestAsync(testProject, "AddTwoNumbers_ReturnsCorrectSum", cts.Token);

        // Set breakpoint on "int result = a + b;" line 11 in MathTests.cs
        await Dbg.SetBreakpointAsync(testsDll, "MathTests.cs", 11, cts.Token);
        await Dbg.ContinueAsync(cts.Token);

        var hit = await DebuggerTestHelpers.WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);
        Assert.NotNull(hit);

        var locals = await Dbg.GetLocalsAsync(0, cts.Token);
        var aVar = locals.FirstOrDefault(v => v.Name == "a");
        Assert.NotNull(aVar);
        Assert.Equal("21", aVar.Value);

        await Dbg.ContinueAsync(cts.Token);
        await DebuggerTestHelpers.DrainToExit(Dbg, cts.Token);
        await Dbg.DisconnectAsync(cts.Token);
    }

    [Fact]
    public async Task AttachAsync_RunningProcess_AttachesSuccessfully()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Launch HelloDebug as a standalone process (not via DotnetDebugger).
        // HelloDebug runs in ~200ms on this machine; we need to attach before it exits.
        // Strategy: poll with short retries until EnumerateCLRs succeeds (CLR loaded)
        // or the process exits. CLR is typically visible within 50-100ms of start.
        using var target = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = HelloDebugDll,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;

        (uint Pid, string ProcessName) result = default;
        Exception? lastEx = null;

        // Retry loop: attempt attach every 30ms for up to ~300ms
        for (int attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(30, cts.Token);

            if (target.HasExited)
                break;

            try
            {
                result = await Dbg.AttachAsync((uint)target.Id, cts.Token);
                lastEx = null;
                break;
            }
            catch (InvalidOperationException ex)
            {
                // EnumerateCLRs not ready yet — keep retrying
                lastEx = ex;
                await Dbg.DisconnectAsync(cts.Token);  // reset debugger state for next attempt
            }
        }

        if (lastEx is not null)
            throw new InvalidOperationException(
                "Could not attach to HelloDebug process within retry window", lastEx);

        if (result == default)
            throw new InvalidOperationException(
                "HelloDebug process exited before attach could succeed");

        Assert.Equal((uint)target.Id, result.Pid);
        Assert.False(string.IsNullOrEmpty(result.ProcessName));

        await Dbg.DisconnectAsync(cts.Token);

        // Process may have already exited (e.g., threw Section 21 exception)
        try { target.Kill(); } catch { /* already exited */ }
    }
}
