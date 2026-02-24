using DebuggerNetMcp.Core.Engine;

namespace DebuggerNetMcp.Tests;

internal static class DebuggerTestHelpers
{
    /// <summary>
    /// Drains events until the requested type arrives. Throws if process exits first.
    /// </summary>
    public static async Task<T> WaitForSpecificEvent<T>(
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
    public static async Task DrainToExit(DotnetDebugger dbg, CancellationToken ct)
    {
        while (true)
        {
            var ev = await dbg.WaitForEventAsync(ct);
            if (ev is ExitedEvent) return;
            if (ev is BreakpointHitEvent or StoppedEvent or ExceptionEvent)
                await dbg.ContinueAsync(ct); // keep process running toward exit
        }
    }
}
