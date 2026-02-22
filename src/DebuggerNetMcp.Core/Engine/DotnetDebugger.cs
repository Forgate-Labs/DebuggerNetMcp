using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Channels;
using DebuggerNetMcp.Core.Interop;

namespace DebuggerNetMcp.Core.Engine;

/// <summary>
/// The .NET debug engine. Wraps ICorDebug via libdbgshim.so to launch/attach to managed processes,
/// set breakpoints, step, and inspect variables.
///
/// Thread model: All ICorDebug API calls happen on the dedicated _debugThread.
/// Async public methods dispatch work to _debugThread via _commandChannel and
/// await TaskCompletionSource results. This prevents COM apartment violations.
///
/// Event model: ICorDebug callbacks post events to _eventChannel (Channel&lt;DebugEvent&gt;).
/// Callers consume events via WaitForEventAsync.
/// </summary>
public sealed class DotnetDebugger : IAsyncDisposable
{
    // -----------------------------------------------------------------------
    // Infrastructure
    // -----------------------------------------------------------------------

    private readonly Channel<DebugEvent> _eventChannel;
    private readonly Channel<Action> _commandChannel;
    private readonly ManagedCallbackHandler _callbackHandler;
    private readonly Thread _debugThread;

    private ICorDebug? _corDebug;
    private ICorDebugProcess? _process;
    private int _nextBreakpointId = 1;

    // Pending breakpoints: set before the module loads
    private readonly List<PendingBreakpoint> _pendingBreakpoints = new();

    // Active breakpoints: ICorDebugFunctionBreakpoint instances by ID
    private readonly Dictionary<int, ICorDebugFunctionBreakpoint> _activeBreakpoints = new();

    // Loaded modules: module name -> ICorDebugModule
    private readonly Dictionary<string, ICorDebugModule> _loadedModules = new(StringComparer.OrdinalIgnoreCase);

    public DotnetDebugger(string? dbgShimPath = null)
    {
        // Event channel: single writer (callback thread), multiple readers (MCP tools)
        _eventChannel = Channel.CreateUnbounded<DebugEvent>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false  // CRITICAL: prevents deadlock
        });

        // Command channel: MCP tool threads enqueue work; _debugThread executes it
        _commandChannel = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
            AllowSynchronousContinuations = false
        });

        _callbackHandler = new ManagedCallbackHandler(_eventChannel.Writer);
        _callbackHandler.OnProcessCreated = process => _process = process;
        _callbackHandler.OnModuleLoaded = OnModuleLoaded;

        // Load libdbgshim.so (throws FileNotFoundException if not found)
        DbgShimInterop.Load(dbgShimPath);

        // Start the dedicated ICorDebug dispatch thread
        _debugThread = new Thread(DebugThreadLoop)
        {
            IsBackground = true,
            Name = "ICorDebug-Dispatch"
        };
        _debugThread.Start();
    }

    // -----------------------------------------------------------------------
    // Public API — Launch, Attach, Disconnect
    // -----------------------------------------------------------------------

    /// <summary>
    /// Builds the target .NET project (-c Debug) then launches it under the debugger.
    /// Returns when ICorDebug is initialized and the process has been created
    /// (i.e., the CreateProcess callback has fired).
    /// </summary>
    /// <param name="projectPath">Path to the .csproj or directory containing one.</param>
    /// <param name="appDllPath">Path to the compiled .dll to run (e.g. bin/Debug/net9.0/App.dll).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task LaunchAsync(string projectPath, string appDllPath,
        CancellationToken ct = default)
    {
        // Step 1: dotnet build -c Debug
        await BuildProjectAsync(projectPath, ct);

        // Step 2: Launch under debugger via command channel (must run on debug thread)
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await DispatchAsync(() =>
        {
            try
            {
                LaunchUnderDebugger(appDllPath);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, ct);

        await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Attaches to a running .NET process by PID.
    /// </summary>
    public async Task AttachAsync(uint processId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await DispatchAsync(() =>
        {
            try
            {
                AttachToProcess(processId);
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, ct);

        await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Stops the debug session and terminates the debuggee if still running.
    /// </summary>
    public async Task DisconnectAsync(CancellationToken ct = default)
    {
        await DispatchAsync(() =>
        {
            try
            {
                _process?.Stop(0);
                _process?.Terminate(0);
            }
            catch { /* process may already be gone */ }
        }, ct);

        // Complete command channel to stop the debug thread
        _commandChannel.Writer.TryComplete();
    }

    /// <summary>
    /// Waits for the next debug event from the ICorDebug callback thread.
    /// </summary>
    public async Task<DebugEvent> WaitForEventAsync(CancellationToken ct = default)
    {
        return await _eventChannel.Reader.ReadAsync(ct);
    }

    // -----------------------------------------------------------------------
    // IAsyncDisposable
    // -----------------------------------------------------------------------

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        if (_debugThread.IsAlive)
            _debugThread.Join(TimeSpan.FromSeconds(2));
    }

    // -----------------------------------------------------------------------
    // Private: Launch + Attach (run on debug thread via DispatchAsync)
    // -----------------------------------------------------------------------

    private void LaunchUnderDebugger(string appDllPath)
    {
        // Create a RuntimeStartupCallback delegate
        RuntimeStartupCallback callback = OnRuntimeStarted;

        // CRITICAL: KeepAlive BEFORE RegisterForRuntimeStartup (kernel 6.12+ SIGSEGV fix)
        DbgShimInterop.KeepAlive(callback);

        string cmdLine = $"dotnet \"{appDllPath}\"";
        int hr = DbgShimInterop.CreateProcessForLaunch(
            cmdLine,
            bSuspendProcess: true,
            IntPtr.Zero,
            null,
            out uint pid,
            out IntPtr resumeHandle);

        if (hr != 0)
            throw new InvalidOperationException($"CreateProcessForLaunch failed: HRESULT 0x{hr:X8}");

        hr = DbgShimInterop.RegisterForRuntimeStartup(pid, callback, IntPtr.Zero, out _);
        if (hr != 0)
        {
            DbgShimInterop.CloseResumeHandle(resumeHandle);
            throw new InvalidOperationException($"RegisterForRuntimeStartup failed: HRESULT 0x{hr:X8}");
        }

        DbgShimInterop.ResumeProcess(resumeHandle);
        DbgShimInterop.CloseResumeHandle(resumeHandle);
    }

    private void AttachToProcess(uint processId)
    {
        RuntimeStartupCallback callback = OnRuntimeStarted;
        DbgShimInterop.KeepAlive(callback);  // CRITICAL: same GC guard for attach

        int hr = DbgShimInterop.RegisterForRuntimeStartup(processId, callback, IntPtr.Zero, out _);
        if (hr != 0)
            throw new InvalidOperationException($"RegisterForRuntimeStartup failed: HRESULT 0x{hr:X8}");
    }

    private void OnRuntimeStarted(IntPtr pCordb, IntPtr parameter, int hr)
    {
        if (hr != 0)
        {
            _eventChannel.Writer.TryWrite(new ExceptionEvent("StartupError",
                $"Runtime startup failed: HRESULT 0x{hr:X8}", 0, true));
            return;
        }

        // Use StrategyBasedComWrappers to wrap the native ICorDebug pointer.
        // Marshal.GetObjectForIUnknown returns a legacy RCW that cannot be cast to
        // [GeneratedComInterface] types (SYSLIB1099) and is Windows-only (CA1416).
        // StrategyBasedComWrappers.GetOrCreateObjectForComInstance is the correct
        // cross-platform path for source-generated COM interop.
        _corDebug = (ICorDebug)new StrategyBasedComWrappers()
            .GetOrCreateObjectForComInstance(pCordb, CreateObjectFlags.UniqueInstance);
        _corDebug.Initialize();
        _corDebug.SetManagedHandler(_callbackHandler);
        // ICorDebugProcess arrives separately via CreateProcess callback
    }

    // -----------------------------------------------------------------------
    // Private: Module loading (invoked from callback thread via OnModuleLoaded)
    // -----------------------------------------------------------------------

    private void OnModuleLoaded(ICorDebugModule module)
    {
        // Get module name — GetName uses IntPtr signature (SYSLIB1051 fix from Plan 02-02)
        uint nameLen = 256;
        IntPtr namePtr = Marshal.AllocHGlobal((int)(nameLen * 2));
        string moduleName;
        try
        {
            module.GetName(nameLen, out _, namePtr);
            moduleName = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
        }
        finally
        {
            Marshal.FreeHGlobal(namePtr);
        }

        _loadedModules[moduleName] = module;

        // Resolve any pending breakpoints for this module
        for (int i = _pendingBreakpoints.Count - 1; i >= 0; i--)
        {
            var pending = _pendingBreakpoints[i];
            if (moduleName.EndsWith(pending.DllName, StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ResolveBreakpoint(module, pending.Id, pending.MethodToken, pending.ILOffset);
                    _pendingBreakpoints.RemoveAt(i);
                }
                catch { /* log and keep pending */ }
            }
        }
    }

    private void ResolveBreakpoint(ICorDebugModule module, int id, int methodToken, int ilOffset)
    {
        module.GetFunctionFromToken((uint)methodToken, out ICorDebugFunction fn);
        fn.CreateBreakpoint(out ICorDebugFunctionBreakpoint bp);
        bp.Activate(1);  // 1 = enabled
        _activeBreakpoints[id] = bp;

        // Register for hit reporting: use the stable methodDef token as key
        // (BreakpointTokenToId key is uint methodDef, per STATE.md decision)
        _callbackHandler.BreakpointTokenToId[(uint)methodToken] = id;
    }

    // -----------------------------------------------------------------------
    // Public API — Breakpoint management
    // -----------------------------------------------------------------------

    /// <summary>
    /// Sets a breakpoint at the given source file and line number.
    /// Returns the breakpoint ID. If the module is not yet loaded, queues as pending.
    /// </summary>
    public async Task<int> SetBreakpointAsync(string dllPath, string sourceFile, int line,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                int id = _nextBreakpointId++;

                // Resolve (methodToken, ilOffset) from the PDB
                int methodToken, ilOffset;
                try
                {
                    (methodToken, ilOffset) = PdbReader.FindLocation(dllPath, sourceFile, line);
                }
                catch (Exception ex)
                {
                    tcs.SetException(new InvalidOperationException(
                        $"Cannot find source location {sourceFile}:{line} in {dllPath}", ex));
                    return;
                }

                string dllName = Path.GetFileName(dllPath);

                // Look for the loaded module
                ICorDebugModule? module = null;
                foreach (var kvp in _loadedModules)
                {
                    if (kvp.Key.EndsWith(dllName, StringComparison.OrdinalIgnoreCase))
                    {
                        module = kvp.Value;
                        break;
                    }
                }

                if (module is not null)
                {
                    ResolveBreakpoint(module, id, methodToken, ilOffset);
                }
                else
                {
                    // Module not loaded yet — queue as pending
                    _pendingBreakpoints.Add(new PendingBreakpoint(id, dllName, methodToken, ilOffset));
                }

                tcs.SetResult(id);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Deactivates and removes a breakpoint by ID.
    /// </summary>
    public async Task RemoveBreakpointAsync(int breakpointId, CancellationToken ct = default)
    {
        await DispatchAsync(() =>
        {
            // Remove from active breakpoints
            if (_activeBreakpoints.TryGetValue(breakpointId, out var bp))
            {
                try { bp.Activate(0); } catch { /* ignore if process is gone */ }
                _activeBreakpoints.Remove(breakpointId);
            }

            // Remove from pending breakpoints
            _pendingBreakpoints.RemoveAll(p => p.Id == breakpointId);
        }, ct);
    }

    // -----------------------------------------------------------------------
    // Private: Build
    // -----------------------------------------------------------------------

    private static async Task BuildProjectAsync(string projectPath, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("dotnet", $"build \"{projectPath}\" -c Debug")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        using var buildProcess = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet build");

        await buildProcess.WaitForExitAsync(ct);

        if (buildProcess.ExitCode != 0)
        {
            string err = await buildProcess.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"dotnet build failed (exit {buildProcess.ExitCode}):\n{err}");
        }
    }

    // -----------------------------------------------------------------------
    // Private: Debug thread loop + command dispatch
    // -----------------------------------------------------------------------

    private void DebugThreadLoop()
    {
        // Block the thread waiting for commands, processing them one at a time
        var reader = _commandChannel.Reader;
        while (true)
        {
            // Use GetAwaiter().GetResult() to block synchronously on the thread
            // (this IS the dedicated thread; blocking here is intentional)
            Action? action = null;
            try
            {
                var valueTask = reader.ReadAsync();
                action = valueTask.IsCompleted
                    ? valueTask.Result
                    : valueTask.AsTask().GetAwaiter().GetResult();
            }
            catch (ChannelClosedException)
            {
                break;  // command channel completed — exit thread
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try { action?.Invoke(); }
            catch (Exception) { /* swallow — action must handle its own errors via TCS */ }
        }
    }

    /// <summary>
    /// Enqueues an action onto the dedicated debug thread and waits for it to be dequeued.
    /// The action runs synchronously on the debug thread.
    /// </summary>
    private async Task DispatchAsync(Action action, CancellationToken ct)
    {
        await _commandChannel.Writer.WriteAsync(action, ct);
    }

    // -----------------------------------------------------------------------
    // Public API — Execution control
    // -----------------------------------------------------------------------

    /// <summary>
    /// Resumes execution of the debuggee process.
    /// </summary>
    public async Task ContinueAsync(CancellationToken ct = default)
    {
        await DispatchAsync(() =>
        {
            _process?.Continue(0);
        }, ct);
    }

    /// <summary>
    /// Pauses the debuggee process.
    /// </summary>
    public async Task PauseAsync(CancellationToken ct = default)
    {
        await DispatchAsync(() =>
        {
            _process?.Stop(0);
        }, ct);
    }

    /// <summary>
    /// Steps over the current source line (does not enter called methods).
    /// </summary>
    public async Task StepOverAsync(CancellationToken ct = default)
        => await StepAsync(stepIn: false, ct: ct);

    /// <summary>
    /// Steps into the current source line (enters called methods).
    /// </summary>
    public async Task StepIntoAsync(CancellationToken ct = default)
        => await StepAsync(stepIn: true, ct: ct);

    /// <summary>
    /// Steps out of the current method (runs until the current method returns).
    /// </summary>
    public async Task StepOutAsync(CancellationToken ct = default)
    {
        await DispatchAsync(() =>
        {
            if (_process is null) return;

            ICorDebugThread thread = GetCurrentThread();

            thread.CreateStepper(out ICorDebugStepper stepper);
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);  // NOT STOP_UNMANAGED
            stepper.StepOut();
            _process.Continue(0);  // Must continue AFTER setting up step
        }, ct);
    }

    private async Task StepAsync(bool stepIn, CancellationToken ct)
    {
        await DispatchAsync(() =>
        {
            if (_process is null) return;

            ICorDebugThread thread = GetCurrentThread();

            thread.CreateStepper(out ICorDebugStepper stepper);
            stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
            stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
            stepper.Step(stepIn ? 1 : 0);  // 1=step-into, 0=step-over
            _process.Continue(0);  // Must continue AFTER setting up step
        }, ct);
    }

    /// <summary>
    /// Gets the first thread from the process thread enumeration.
    /// Must be called on the debug thread.
    /// </summary>
    private ICorDebugThread GetCurrentThread()
    {
        _process!.EnumerateThreads(out ICorDebugThreadEnum threadEnum);
        var threads = new ICorDebugThread[1];
        threadEnum.Next(1, threads, out uint fetched);
        if (fetched == 0)
            throw new InvalidOperationException("No threads found in process");
        return threads[0];
    }

    // -----------------------------------------------------------------------
    // Public API — Inspection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Returns a snapshot of the current call stack.
    /// Must be called while the debuggee is stopped (at a breakpoint, step, or pause).
    /// </summary>
    public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<StackFrameInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                var frames = new List<StackFrameInfo>();
                ICorDebugThread thread = GetCurrentThread();

                // Enumerate chains on the thread
                thread.EnumerateChains(out ICorDebugChainEnum chainEnum);
                var chains = new ICorDebugChain[1];
                int frameIndex = 0;

                while (true)
                {
                    chainEnum.Next(1, chains, out uint chainFetched);
                    if (chainFetched == 0) break;

                    chains[0].EnumerateFrames(out ICorDebugFrameEnum frameEnum);
                    var frameArr = new ICorDebugFrame[1];

                    while (true)
                    {
                        frameEnum.Next(1, frameArr, out uint frameFetched);
                        if (frameFetched == 0) break;

                        var frame = frameArr[0];
                        try
                        {
                            if (frame is ICorDebugILFrame ilFrame)
                            {
                                ilFrame.GetIP(out uint ip, out _);
                                frame.GetFunction(out ICorDebugFunction fn);
                                fn.GetToken(out uint methodToken);
                                fn.GetModule(out ICorDebugModule module);

                                uint nameLen = 512;
                                IntPtr namePtr = Marshal.AllocHGlobal((int)(nameLen * 2));
                                string dllPath;
                                try
                                {
                                    module.GetName(nameLen, out _, namePtr);
                                    dllPath = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                                }
                                finally
                                {
                                    Marshal.FreeHGlobal(namePtr);
                                }

                                // Try to get source location from PDB (best-effort)
                                string? sourceFile = null;
                                int? sourceLine = null;
                                // TODO: add PdbReader.FindSourceLocation(dllPath, methodToken, ilOffset)
                                // for reverse IL-offset → source line mapping. Deferred to future work.

                                frames.Add(new StackFrameInfo(
                                    frameIndex++,
                                    $"0x{methodToken:X8}",
                                    sourceFile,
                                    sourceLine,
                                    (int)ip));
                            }
                            else
                            {
                                frameIndex++;
                            }
                        }
                        catch { frameIndex++; /* skip unreadable frames */ }
                    }
                }

                tcs.SetResult(frames);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Returns local variables in the current stack frame.
    /// Uses PDB slot-to-name mapping and ICorDebugILFrame.GetLocalVariable for values.
    /// Must be called while stopped.
    /// </summary>
    public async Task<IReadOnlyList<VariableInfo>> GetLocalsAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<VariableInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                const int CORDBG_E_IL_VAR_NOT_AVAILABLE = unchecked((int)0x80131304);
                var result = new List<VariableInfo>();

                ICorDebugThread thread = GetCurrentThread();
                thread.GetActiveFrame(out ICorDebugFrame frame);

                if (frame is not ICorDebugILFrame ilFrame)
                {
                    tcs.SetResult(result);  // native frame — no locals
                    return;
                }

                // Get the method token and dll path for PDB-based name lookup
                frame.GetFunction(out ICorDebugFunction fn);
                fn.GetToken(out uint methodToken);
                fn.GetModule(out ICorDebugModule module);

                uint nameLen = 512;
                IntPtr namePtr = Marshal.AllocHGlobal((int)(nameLen * 2));
                string dllPath;
                try
                {
                    module.GetName(nameLen, out _, namePtr);
                    dllPath = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }

                // Get local variable names from PDB (slot index → name)
                Dictionary<int, string> localNames = new();
                try
                {
                    localNames = PdbReader.GetLocalNames(dllPath, (int)methodToken);
                }
                catch { /* PDB not available — use generic names */ }

                // Enumerate locals by index; CORDBG_E_IL_VAR_NOT_AVAILABLE signals end
                for (uint i = 0; i < 256; i++)
                {
                    try
                    {
                        ilFrame.GetLocalVariable(i, out ICorDebugValue val);
                        string varName = localNames.TryGetValue((int)i, out string? n) ? n : $"local_{i}";
                        result.Add(VariableReader.ReadValue(varName, val));
                    }
                    catch (COMException ex) when (ex.HResult == CORDBG_E_IL_VAR_NOT_AVAILABLE)
                    {
                        break;  // No more variables
                    }
                    catch
                    {
                        break;  // Other errors — stop enumeration
                    }
                }

                tcs.SetResult(result);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Evaluates a simple expression in the context of the current frame.
    /// Currently supports local variable lookup by name only (full expression eval requires
    /// ICorDebugEval which needs a running runtime — deferred to future work).
    /// </summary>
    public async Task<EvalResult> EvaluateAsync(string expression, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<EvalResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                const int CORDBG_E_IL_VAR_NOT_AVAILABLE = unchecked((int)0x80131304);

                ICorDebugThread thread = GetCurrentThread();
                thread.GetActiveFrame(out ICorDebugFrame frame);

                if (frame is not ICorDebugILFrame ilFrame)
                {
                    tcs.SetResult(new EvalResult(false, string.Empty, "No IL frame available"));
                    return;
                }

                frame.GetFunction(out ICorDebugFunction fn);
                fn.GetToken(out uint methodToken);
                fn.GetModule(out ICorDebugModule module);

                uint nameLen = 512;
                IntPtr namePtr = Marshal.AllocHGlobal((int)(nameLen * 2));
                string dllPath;
                try
                {
                    module.GetName(nameLen, out _, namePtr);
                    dllPath = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                }
                finally
                {
                    Marshal.FreeHGlobal(namePtr);
                }

                Dictionary<int, string> localNames = new();
                try { localNames = PdbReader.GetLocalNames(dllPath, (int)methodToken); }
                catch { }

                // Find the slot matching the expression (variable name lookup)
                int? matchedSlot = null;
                foreach (var kv in localNames)
                {
                    if (kv.Value.Equals(expression, StringComparison.Ordinal))
                    {
                        matchedSlot = kv.Key;
                        break;
                    }
                }

                if (matchedSlot is null)
                {
                    tcs.SetResult(new EvalResult(false, string.Empty,
                        $"Variable '{expression}' not found in current scope"));
                    return;
                }

                try
                {
                    ilFrame.GetLocalVariable((uint)matchedSlot.Value, out ICorDebugValue val);
                    var varInfo = VariableReader.ReadValue(expression, val);
                    tcs.SetResult(new EvalResult(true, varInfo.Value, null));
                }
                catch (COMException ex) when (ex.HResult == CORDBG_E_IL_VAR_NOT_AVAILABLE)
                {
                    tcs.SetResult(new EvalResult(false, string.Empty,
                        "Variable not available at this location"));
                }
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    // -----------------------------------------------------------------------
    // Private: Pending breakpoint record
    // -----------------------------------------------------------------------

    private record PendingBreakpoint(int Id, string DllName, int MethodToken, int ILOffset);
}
