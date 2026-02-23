using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using DebuggerNetMcp.Core.Engine;

[McpServerToolType]
public sealed class DebuggerTools(DotnetDebugger debugger)
{
    private string _state = "idle";  // idle | running | stopped | exited

    // -----------------------------------------------------------------------
    // Private helpers
    // -----------------------------------------------------------------------

    private static object SerializeEvent(DebugEvent ev) => ev switch
    {
        StoppedEvent e       => new { type = "stopped",       reason = e.Reason, threadId = e.ThreadId, topFrame = (object?)e.TopFrame },
        BreakpointHitEvent e => new { type = "breakpointHit", breakpointId = e.BreakpointId, threadId = e.ThreadId, topFrame = (object)e.TopFrame },
        ExceptionEvent e     => new { type = "exception",     exceptionType = e.ExceptionType, message = e.Message, threadId = e.ThreadId, isUnhandled = e.IsUnhandled },
        ExitedEvent e        => new { type = "exited",        exitCode = e.ExitCode },
        OutputEvent e        => new { type = "output",        category = e.Category, output = e.Output },
        _                    => new { type = "unknown" }
    };

    private async Task<string> RunAndWait(Func<Task> operation, CancellationToken ct)
    {
        try
        {
            await operation();
            _state = "running";
            var ev = await debugger.WaitForEventAsync(ct);
            _state = ev is ExitedEvent ? "exited" : "stopped";
            return JsonSerializer.Serialize(new { success = true, state = _state, @event = SerializeEvent(ev) });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    // -----------------------------------------------------------------------
    // Session management tools
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "debug_launch"),
     Description("Build and launch a .NET project under the debugger. " +
                 "Returns when the process is created. Use debug_continue to run until a breakpoint.")]
    public async Task<string> Launch(
        [Description("Path to the .csproj file or project directory")] string projectPath,
        [Description("Path to the compiled .dll to debug (e.g. bin/Debug/net10.0/App.dll)")] string appDllPath,
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

    [McpServerTool(Name = "debug_attach"),
     Description("Attach the debugger to a running .NET process by process ID.")]
    public async Task<string> Attach(
        [Description("The process ID to attach to")] uint processId,
        CancellationToken ct)
    {
        try
        {
            await debugger.AttachAsync(processId, ct);
            _state = "running";
            return JsonSerializer.Serialize(new { success = true, state = _state, pid = processId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "debug_disconnect"),
     Description("Disconnect from the debuggee and end the debug session.")]
    public async Task<string> Disconnect(CancellationToken ct)
    {
        try
        {
            await debugger.DisconnectAsync(ct);
            _state = "idle";
            return JsonSerializer.Serialize(new { success = true, state = _state });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "debug_status"),
     Description("Returns the current debugger state: idle (no session), running (process running), stopped (at breakpoint or step), or exited (process terminated).")]
    public Task<string> GetStatus(CancellationToken ct) =>
        Task.FromResult(JsonSerializer.Serialize(new { state = _state }));

    // -----------------------------------------------------------------------
    // Breakpoint tools
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "debug_set_breakpoint"),
     Description("Set a breakpoint at a source file line. Returns the breakpoint ID needed to remove it later.")]
    public async Task<string> SetBreakpoint(
        [Description("Full path to the compiled .dll")] string dllPath,
        [Description("Source file name (e.g. Program.cs)")] string sourceFile,
        [Description("1-based source line number")] int line,
        CancellationToken ct)
    {
        try
        {
            var id = await debugger.SetBreakpointAsync(dllPath, sourceFile, line, ct);
            return JsonSerializer.Serialize(new { success = true, id, file = sourceFile, line });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    [McpServerTool(Name = "debug_remove_breakpoint"),
     Description("Remove a previously set breakpoint by its ID.")]
    public async Task<string> RemoveBreakpoint(
        [Description("Breakpoint ID returned by debug_set_breakpoint")] int breakpointId,
        CancellationToken ct)
    {
        try
        {
            await debugger.RemoveBreakpointAsync(breakpointId, ct);
            return JsonSerializer.Serialize(new { success = true, id = breakpointId });
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }

    // -----------------------------------------------------------------------
    // Execution control tools
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "debug_continue"),
     Description("Resume execution and wait for the next debug event (breakpoint hit, step complete, exception, or exit). Returns the event.")]
    public Task<string> Continue(CancellationToken ct) =>
        RunAndWait(() => debugger.ContinueAsync(ct), ct);

    [McpServerTool(Name = "debug_step_over"),
     Description("Step over the current source line without entering called methods. Returns the resulting debug event.")]
    public Task<string> StepOver(CancellationToken ct) =>
        RunAndWait(() => debugger.StepOverAsync(ct), ct);

    [McpServerTool(Name = "debug_step_into"),
     Description("Step into the current source line, entering any called methods. Returns the resulting debug event.")]
    public Task<string> StepInto(CancellationToken ct) =>
        RunAndWait(() => debugger.StepIntoAsync(ct), ct);

    [McpServerTool(Name = "debug_step_out"),
     Description("Step out of the current method and return to the caller. Returns the resulting debug event.")]
    public Task<string> StepOut(CancellationToken ct) =>
        RunAndWait(() => debugger.StepOutAsync(ct), ct);

    [McpServerTool(Name = "debug_pause"),
     Description("Pause a running process and wait for the stopped event.")]
    public Task<string> Pause(CancellationToken ct) =>
        RunAndWait(() => debugger.PauseAsync(ct), ct);

    // -----------------------------------------------------------------------
    // Inspection tools
    // -----------------------------------------------------------------------

    [McpServerTool(Name = "debug_variables"),
     Description("Get local variables at the current stopped position. Requires the process to be stopped at a breakpoint or step.")]
    public async Task<string> GetVariables(CancellationToken ct)
    {
        try
        {
            var locals = await debugger.GetLocalsAsync(ct);
            return JsonSerializer.Serialize(locals);
        }
        catch (Exception ex)
        {
            var hint = ex.Message.Contains("80131301", StringComparison.OrdinalIgnoreCase)
                ? " Process not stopped. Call debug_continue or debug_step_* first."
                : string.Empty;
            return JsonSerializer.Serialize(new { success = false, error = ex.Message + hint });
        }
    }

    [McpServerTool(Name = "debug_stacktrace"),
     Description("Get the current call stack. Requires the process to be stopped at a breakpoint or step.")]
    public async Task<string> GetStackTrace(CancellationToken ct)
    {
        try
        {
            var frames = await debugger.GetStackTraceAsync(ct);
            return JsonSerializer.Serialize(frames);
        }
        catch (Exception ex)
        {
            var hint = ex.Message.Contains("80131301", StringComparison.OrdinalIgnoreCase)
                ? " Process not stopped. Call debug_continue or debug_step_* first."
                : string.Empty;
            return JsonSerializer.Serialize(new { success = false, error = ex.Message + hint });
        }
    }

    [McpServerTool(Name = "debug_evaluate"),
     Description("Evaluate a local variable name at the current stopped position. Requires the process to be stopped.")]
    public async Task<string> Evaluate(
        [Description("Variable name or simple expression to evaluate")] string expression,
        CancellationToken ct)
    {
        try
        {
            var result = await debugger.EvaluateAsync(expression, ct);
            return JsonSerializer.Serialize(result);
        }
        catch (Exception ex)
        {
            return JsonSerializer.Serialize(new { success = false, error = ex.Message });
        }
    }
}
