using System.Diagnostics;
using System.Linq;
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

    private Channel<DebugEvent> _eventChannel;
    private readonly Channel<Action> _commandChannel;
    private readonly ManagedCallbackHandler _callbackHandler;
    private readonly Thread _debugThread;

    private ICorDebug? _corDebug;
    private ICorDebugProcess? _process;
    private uint _launchedPid;
    private uint _attachPid;
    private int _nextBreakpointId = 1;

    // dotnet test vstest runner process (kept alive while testhost is being debugged)
    private System.Diagnostics.Process? _dotnetTestProcess;

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
        bool notifyFirstChanceExceptions = false,
        CancellationToken ct = default)
    {
        // Clean up any previous session (terminates the old process and clears module/breakpoint state).
        // Set SuppressExitProcess so the old process's ExitProcess callback does not TryComplete
        // the new session's event channel (the callback fires asynchronously after Terminate).
        if (_process is not null)
        {
            _callbackHandler.SuppressExitProcess = true;
            await DisconnectAsync(ct);
        }

        // Always recreate the event channel for each new session.
        _eventChannel = Channel.CreateUnbounded<DebugEvent>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
        _callbackHandler.UpdateEventWriter(_eventChannel.Writer);

        // Step 1: dotnet build -c Debug
        await BuildProjectAsync(projectPath, ct);

        // Step 2: Launch under debugger via command channel (must run on debug thread).
        // Set StopAtCreateProcess so the process halts at the CreateProcess event and emits a
        // StoppedEvent("process_created") before any user code runs.  This lets the caller
        // configure breakpoints (as pending, since modules load after the first Continue).
        _callbackHandler.StopAtCreateProcess = true;
        _callbackHandler.NotifyFirstChanceExceptions = notifyFirstChanceExceptions;
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
                _callbackHandler.StopAtCreateProcess = false;
                tcs.SetException(ex);
            }
        }, ct);

        await tcs.Task.WaitAsync(ct);

        // Step 3: Wait for the CreateProcess stopping event (or a startup error).
        var startupEvent = await WaitForEventAsync(ct);
        if (startupEvent is ExceptionEvent exc)
            throw new InvalidOperationException($"{exc.ExceptionType}: {exc.Message}");
        // On StoppedEvent("process_created") the process is suspended.
        // The caller should now set breakpoints and then call ContinueAsync.
    }

    /// <summary>
    /// Attaches to a running .NET process by PID. Waits for the CreateProcess callback to confirm
    /// the attach succeeded and _process is set before returning.
    /// Returns (Pid, ProcessName) — process continues running (not stopped) after attach.
    /// </summary>
    public async Task<(uint Pid, string ProcessName)> AttachAsync(
        uint processId, CancellationToken ct = default)
    {
        // Clean up any previous session so _launchedPid / _corDebug / _process are reset.
        // Without this, OnRuntimeStarted uses _launchedPid from the old session and calls
        // DebugActiveProcess on the wrong PID, causing the CreateProcess callback to never fire.
        if (_process is not null || _launchedPid != 0)
        {
            _callbackHandler.SuppressExitProcess = true;
            await DisconnectAsync(ct);
        }

        var attachConfirmedTcs = new TaskCompletionSource<uint>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        // Set OnProcessCreated BEFORE dispatching — the callback fires on the ICorDebug thread
        // and may complete very quickly (race condition if set after dispatch).
        // Also set _process here (same as default constructor handler) so subsequent API calls work.
        _callbackHandler.OnProcessCreated = proc =>
        {
            _process = proc;
            proc.GetID(out uint pid);
            attachConfirmedTcs.TrySetResult(pid);
        };

        await DispatchAsync(() =>
        {
            try
            {
                AttachToProcess(processId);
            }
            catch (Exception ex)
            {
                attachConfirmedTcs.TrySetException(ex);
            }
        }, ct);

        // Wait for the CreateProcess callback to fire — confirms _process is set
        uint confirmedPid = await attachConfirmedTcs.Task.WaitAsync(ct);

        // Read process name outside the debug thread (safe: just reading OS process table)
        string processName;
        try
        {
            processName = System.Diagnostics.Process.GetProcessById((int)confirmedPid).ProcessName;
        }
        catch
        {
            processName = "unknown";
        }

        return (confirmedPid, processName);
    }

    /// <summary>
    /// Builds and launches a test project with VSTEST_HOST_DEBUG=1, parses the testhost PID
    /// from stdout, then attaches to it. Returns the same (Pid, ProcessName) as AttachAsync.
    /// </summary>
    /// <param name="projectPath">Path to the xUnit .csproj or project directory.</param>
    /// <param name="filter">Optional --filter expression (e.g. 'FullyQualifiedName~MyTest').</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<(uint Pid, string ProcessName)> LaunchTestAsync(
        string projectPath,
        string? filter = null,
        CancellationToken ct = default)
    {
        await DisconnectAsync(ct);

        // Always recreate the event channel for the new session (same as LaunchAsync).
        _eventChannel = Channel.CreateUnbounded<DebugEvent>(new UnboundedChannelOptions
        {
            SingleWriter = true,
            SingleReader = false,
            AllowSynchronousContinuations = false
        });
        _callbackHandler.UpdateEventWriter(_eventChannel.Writer);

        // Stop at CreateProcess so the caller can set breakpoints before test execution begins.
        _callbackHandler.StopAtCreateProcess = true;

        // Step 1: dotnet build -c Debug
        var buildPsi = new System.Diagnostics.ProcessStartInfo("dotnet",
            $"build \"{projectPath}\" -c Debug")
        {
            UseShellExecute = false,
            RedirectStandardOutput = false,
        };
        using var buildProc = System.Diagnostics.Process.Start(buildPsi)!;
        await buildProc.WaitForExitAsync(ct);
        if (buildProc.ExitCode != 0)
            throw new InvalidOperationException($"dotnet build failed with exit code {buildProc.ExitCode}");

        // Step 2: Launch dotnet test with VSTEST_HOST_DEBUG=1
        string testArgs = $"test \"{projectPath}\" --no-build";
        if (!string.IsNullOrEmpty(filter))
            testArgs += $" --filter \"{filter}\"";

        var testPsi = new System.Diagnostics.ProcessStartInfo("dotnet", testArgs)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
        };
        testPsi.Environment["VSTEST_HOST_DEBUG"] = "1";

        _dotnetTestProcess = System.Diagnostics.Process.Start(testPsi)!;

        // Step 3: Parse testhost PID from stdout
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        uint testhostPid = 0;
        try
        {
            string? line;
            while ((line = await _dotnetTestProcess.StandardOutput.ReadLineAsync(linkedCts.Token)) != null)
            {
                var match = System.Text.RegularExpressions.Regex.Match(line, @"Process Id:\s*(\d+)");
                if (match.Success)
                {
                    testhostPid = uint.Parse(match.Groups[1].Value);
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _dotnetTestProcess.Kill(entireProcessTree: true);
            _dotnetTestProcess = null;
            throw new InvalidOperationException(
                "Failed to get testhost PID from dotnet test output within 25 seconds. " +
                "Ensure the project is a valid xUnit test project.");
        }

        if (testhostPid == 0)
        {
            _dotnetTestProcess.Kill(entireProcessTree: true);
            _dotnetTestProcess = null;
            throw new InvalidOperationException("Failed to get testhost PID from dotnet test output.");
        }

        // Step 4: Attach to testhost — reuses all existing attach infrastructure
        var (pid, processName) = await AttachAsync(testhostPid, ct);

        // Wait for the CreateProcess stopping event — same pattern as LaunchAsync.
        // Process is now suspended; caller must set breakpoints then call ContinueAsync.
        var startupEvent = await WaitForEventAsync(ct);
        if (startupEvent is ExceptionEvent exc)
            throw new InvalidOperationException($"{exc.ExceptionType}: {exc.Message}");

        return (pid, processName);
    }

    /// <summary>
    /// Stops the debug session and terminates the debuggee if still running.
    /// Does NOT close the command channel so the debugger can be reused for a new session.
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
            _process = null;
            _corDebug = null;
            _launchedPid = 0;
            _attachPid = 0;
            // Kill the vstest runner if this was a test session
            if (_dotnetTestProcess is { HasExited: false })
            {
                try { _dotnetTestProcess.Kill(entireProcessTree: true); } catch { }
            }
            _dotnetTestProcess = null;
            _loadedModules.Clear();
            _pendingBreakpoints.Clear();
            _activeBreakpoints.Clear();
            _nextBreakpointId = 1;
            _callbackHandler.NotifyFirstChanceExceptions = false;
            _callbackHandler.ClearKnownThreadIds();
        }, ct);
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
        // Close the command channel to stop the debug thread
        _commandChannel.Writer.TryComplete();
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

        _launchedPid = pid;

        // Use RegisterForRuntimeStartup (bSuspendProcess=false).
        // RegisterForRuntimeStartup3 with bSuspendProcess=true causes SIGSEGV in netcoredbg's
        // libdbgshim.so. bSuspendProcess=false works correctly: libdbgshim polls for CLR startup,
        // and we resume the process immediately after — the race condition is handled by the
        // strace wrapper around the MCP server binary (kernel 6.12+ fix).
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
        // RegisterForRuntimeStartup only works while the CLR is starting up.
        // For already-running managed processes use EnumerateCLRs → CreateVersionStringFromModule
        // → CreateDebuggingInterfaceFromVersionEx to obtain an ICorDebug for the live CLR,
        // then call DebugActiveProcess (done in OnRuntimeStarted) to register the debugger.
        _attachPid = processId;

        int hr = DbgShimInterop.EnumerateCLRs(processId,
            out IntPtr pHandleArray, out IntPtr pStringArray, out uint count);
        if (hr != 0)
            throw new InvalidOperationException($"EnumerateCLRs failed: HRESULT 0x{hr:X8}");

        try
        {
            if (count == 0)
                throw new InvalidOperationException(
                    $"No CLR found in process {processId}. Is it a .NET process that has loaded CoreCLR?");

            // Read the first module path (Unicode string pointer) from the string-pointer array.
            IntPtr firstStringPtr = Marshal.ReadIntPtr(pStringArray, 0);
            string modulePath = Marshal.PtrToStringUni(firstStringPtr) ?? string.Empty;

            // Build the version string that CreateDebuggingInterfaceFromVersionEx expects.
            IntPtr versionBuf = Marshal.AllocHGlobal(512);  // 256 wide chars
            try
            {
                hr = DbgShimInterop.CreateVersionStringFromModule(
                    processId, modulePath, versionBuf, 256, out _);
                if (hr != 0)
                    throw new InvalidOperationException(
                        $"CreateVersionStringFromModule failed: HRESULT 0x{hr:X8}");

                string versionString = Marshal.PtrToStringUni(versionBuf) ?? string.Empty;

                // CorDebugVersion_4_0 = 4 (used by all .NET Core / .NET 5+ debuggers)
                hr = DbgShimInterop.CreateDebuggingInterfaceFromVersionEx(
                    4, versionString, out IntPtr pCordb);
                if (hr != 0)
                    throw new InvalidOperationException(
                        $"CreateDebuggingInterfaceFromVersionEx failed: HRESULT 0x{hr:X8}");

                // Hand off to the same OnRuntimeStarted path used by launch.
                OnRuntimeStarted(pCordb, IntPtr.Zero, 0);
            }
            finally
            {
                Marshal.FreeHGlobal(versionBuf);
            }
        }
        finally
        {
            DbgShimInterop.CloseCLREnumeration(pHandleArray, pStringArray, count);
        }
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

        // Kick off the ICorDebug event loop by registering the target process.
        // DebugActiveProcess queues the initial CreateProcess / LoadModule / CreateThread callbacks.
        if (_launchedPid != 0)
        {
            // Launch path: process was created suspended; Continue(0) lets it run past the
            // initial CreateProcess event (StopAtCreateProcess may pause it again immediately).
            try
            {
                _corDebug.DebugActiveProcess(_launchedPid, 0, out ICorDebugProcess proc);
                proc.Continue(0);
            }
            catch { /* ignore — ICorDebug may deliver events automatically */ }
        }
        else if (_attachPid != 0)
        {
            // Attach path: process is already running. DebugActiveProcess registers the debugger
            // and delivers initial sync callbacks. Do NOT call Continue here — the CreateProcess
            // callback (StopAtCreateProcess=false for attach) will call pProcess.Continue(0).
            // Do NOT catch exceptions — let them propagate to AttachToProcess → TrySetException.
            _corDebug.DebugActiveProcess(_attachPid, 0, out _);
        }
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

        // Use ICorDebugCode.CreateBreakpoint(offset) to set at exact IL offset.
        // fn.CreateBreakpoint() only sets at the function entry point (offset 0),
        // which caused breakpoints to be ignored in .NET 10 where the JIT behavior differs.
        fn.GetILCode(out ICorDebugCode ilCode);
        ilCode.CreateBreakpoint((uint)ilOffset, out ICorDebugFunctionBreakpoint bp);
        bp.Activate(1);  // 1 = enabled
        _activeBreakpoints[id] = bp;

        // Register for hit reporting: use the stable methodDef token as key
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
        // Use the thread ID captured at the last stopping event (Breakpoint/StepComplete/Break).
        // This avoids ICorDebugThreadEnum.Next() which has LPArray marshaling issues with
        // source-generated COM interop ([MarshalAs(LPArray, SizeParamIndex)] for COM interfaces).
        uint tid = _callbackHandler.CurrentStoppedThreadId;
        if (tid == 0)
            throw new InvalidOperationException("No current stopped thread (call debug_continue or debug_step_* first)");

        _process!.GetThread(tid, out ICorDebugThread thread);
        if (thread is null)
            throw new InvalidOperationException($"Thread {tid} not found in process");
        return thread;
    }

    /// <summary>
    /// Returns all threads in the process using a celt=1 loop.
    /// MUST be called on the debug thread (inside DispatchAsync).
    /// Uses celt=1 to avoid ICorDebugThreadEnum.Next LPArray marshaling issues with
    /// source-generated COM interop — the same pattern used for chain/frame enumeration.
    /// </summary>
    private List<ICorDebugThread> GetAllThreads()
    {
        var threads = new List<ICorDebugThread>();
        if (_process is null) return threads;

        // ICorDebugProcess.EnumerateThreads has COM interop issues on Linux with source-generated
        // wrappers (LPArray marshaling). Use KnownThreadIds tracked via CreateThread/ExitThread
        // callbacks and resolve each ID via GetThread(id) which works reliably.
        foreach (uint tid in _callbackHandler.KnownThreadIds)
        {
            try
            {
                _process.GetThread(tid, out ICorDebugThread thread);
                if (thread is not null)
                    threads.Add(thread);
            }
            catch { /* thread may have exited between callback and here */ }
        }
        return threads;
    }

    /// <summary>
    /// Returns a specific thread by ID, or throws if not found.
    /// MUST be called on the debug thread.
    /// </summary>
    private ICorDebugThread GetThreadById(uint threadId)
    {
        _process!.GetThread(threadId, out ICorDebugThread thread);
        if (thread is null)
            throw new InvalidOperationException($"Thread {threadId} not found in process");
        return thread;
    }

    // -----------------------------------------------------------------------
    // Public API — Inspection
    // -----------------------------------------------------------------------

    /// <summary>
    /// Walks the chain/frame tree for a single thread. MUST be called on the debug thread.
    /// </summary>
    private List<StackFrameInfo> GetStackFramesForThread(ICorDebugThread thread)
    {
        var frames = new List<StackFrameInfo>();
        // Walk via GetActiveFrame + GetCaller — avoids EnumerateChains COM interop issues.
        thread.GetActiveFrame(out ICorDebugFrame? current);
        int frameIndex = 0;
        const int maxFrames = 64;

        while (current is not null && frameIndex < maxFrames)
        {
            try
            {
                if (current is ICorDebugILFrame ilFrame)
                {
                    ilFrame.GetIP(out uint ip, out _);
                    current.GetFunction(out ICorDebugFunction fn);
                    fn.GetToken(out uint methodToken);

                    string? sourceFile = null;
                    int? sourceLine = null;
                    string methodName = $"0x{methodToken:X8}"; // fallback

                    try
                    {
                        fn.GetModule(out ICorDebugModule module);
                        string dllPath = VariableReader.GetModulePath(module);
                        if (!string.IsNullOrEmpty(dllPath))
                        {
                            // Resolve source location via PDB reverse lookup
                            var loc = PdbReader.ReverseLookup(dllPath, (int)methodToken, (int)ip);
                            if (loc.HasValue)
                            {
                                sourceFile = Path.GetFileName(loc.Value.sourceFile);
                                sourceLine = loc.Value.line;
                            }

                            // Resolve method name from PE metadata
                            var (resolvedName, _) = PdbReader.GetMethodTypeFields(dllPath, (int)methodToken);
                            if (!string.IsNullOrEmpty(resolvedName))
                                methodName = resolvedName;
                        }
                    }
                    catch { /* non-fatal: framework frames have no PDB; fall back to hex token */ }

                    frames.Add(new StackFrameInfo(frameIndex++, methodName, sourceFile, sourceLine, (int)ip));
                }
                else
                {
                    frameIndex++; // native frame — skip
                }

                current.GetCaller(out ICorDebugFrame? caller);
                current = caller;
            }
            catch { break; }
        }
        return frames;
    }

    /// <summary>
    /// Returns a snapshot of the current call stack.
    /// Must be called while the debuggee is stopped (at a breakpoint, step, or pause).
    /// When threadId is 0, uses the current stopped thread.
    /// </summary>
    public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(
        uint threadId = 0, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<StackFrameInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                ICorDebugThread thread = threadId != 0
                    ? GetThreadById(threadId)
                    : GetCurrentThread();
                tcs.SetResult(GetStackFramesForThread(thread));
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Returns the call stack for every active thread in the process.
    /// Each element is (ThreadId, Frames). MUST be called while stopped.
    /// </summary>
    public async Task<IReadOnlyList<(uint ThreadId, IReadOnlyList<StackFrameInfo> Frames)>>
        GetAllThreadStackTracesAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<(uint, IReadOnlyList<StackFrameInfo>)>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                var result = new List<(uint, IReadOnlyList<StackFrameInfo>)>();
                var threads = GetAllThreads();
                // Fallback: if KnownThreadIds is empty (e.g. attached process before callbacks fired),
                // include at least the current stopped thread (set by last stopping callback).
                if (threads.Count == 0)
                {
                    try { threads.Add(GetCurrentThread()); } catch { }
                }
                // Second fallback: if still empty (Stop() was called, no callback-tracked thread),
                // try EnumerateThreads as a last resort despite its COM interop unreliability.
                if (threads.Count == 0 && _process is not null)
                {
                    try
                    {
                        _process.EnumerateThreads(out ICorDebugThreadEnum threadEnum);
                        if (threadEnum is not null)
                        {
                            var arr2 = new ICorDebugThread[1];
                            while (true)
                            {
                                threadEnum.Next(1, arr2, out uint f);
                                if (f == 0 || arr2[0] is null) break;
                                threads.Add(arr2[0]);
                            }
                        }
                    }
                    catch { }
                }
                foreach (var thread in threads)
                {
                    try
                    {
                        thread.GetID(out uint tid);
                        var frames = GetStackFramesForThread(thread);
                        result.Add((tid, frames));
                    }
                    catch { /* skip threads that can't be inspected */ }
                }
                tcs.SetResult(result);
            }
            catch (Exception ex) { tcs.SetException(ex); }
        }, ct);

        return await tcs.Task.WaitAsync(ct);
    }

    /// <summary>
    /// Returns local variables in the current stack frame.
    /// Uses PDB slot-to-name mapping and ICorDebugILFrame.GetLocalVariable for values.
    /// Must be called while stopped. When threadId is 0, uses the current stopped thread.
    /// </summary>
    public async Task<IReadOnlyList<VariableInfo>> GetLocalsAsync(
        uint threadId = 0, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<IReadOnlyList<VariableInfo>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await DispatchAsync(() =>
        {
            try
            {
                const int CORDBG_E_IL_VAR_NOT_AVAILABLE = unchecked((int)0x80131304);
                var result = new List<VariableInfo>();

                ICorDebugThread thread = threadId != 0 ? GetThreadById(threadId) : GetCurrentThread();
                thread.GetActiveFrame(out ICorDebugFrame frame);

                if (frame is null || frame is not ICorDebugILFrame ilFrame)
                {
                    tcs.SetResult(result);  // native frame — no locals
                    return;
                }

                // Get the method token and dll path for PDB-based name lookup
                frame.GetFunction(out ICorDebugFunction fn);
                if (fn is null) { tcs.SetResult(result); return; }
                fn.GetToken(out uint methodToken);
                fn.GetModule(out ICorDebugModule module);
                if (module is null) { tcs.SetResult(result); return; }

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

                // Check if we're in a state machine's MoveNext — if so, read 'this' fields
                // (C# async variables are stored as fields of the state machine struct, not as IL locals)
                bool readFromStateMachine = false;
                var (smMethodName, smTypeFields) = PdbReader.GetMethodTypeFields(dllPath, (int)methodToken);
                // NEW: closure display class detection
                bool isClosureMethod = smMethodName.Contains(">b__");
                if ((smMethodName == "MoveNext" || isClosureMethod) && smTypeFields.Count > 0)
                {
                    try
                    {
                        // Argument 0 is 'this' — the state machine object/struct reference
                        ilFrame.GetArgument(0, out ICorDebugValue thisArg);
                        if (thisArg != null)
                        {
                            // Dereference reference types (Class, Object) or ByRef managed pointers
                            ICorDebugValue actualThis = thisArg;
                            thisArg.GetType(out uint argTypeRaw);
                            var argElemType = (CorElementType)argTypeRaw;
                            if (argElemType == CorElementType.ByRef ||
                                argElemType == CorElementType.Class ||
                                argElemType == CorElementType.Object)
                            {
                                var refVal = (ICorDebugReferenceValue)thisArg;
                                refVal.IsNull(out int isNullThis);
                                if (isNullThis != 0)
                                    throw new InvalidOperationException("state machine this is null");
                                refVal.Dereference(out actualThis);
                            }

                            if (actualThis is ICorDebugObjectValue objVal)
                            {
                                objVal.GetClass(out ICorDebugClass cls);
                                foreach (var (fieldToken, fieldName) in smTypeFields)
                                {
                                    // Iterator current value: expose as "Current" instead of skipping
                                    string displayName = fieldName;
                                    if (fieldName == "<>2__current")
                                    {
                                        displayName = "Current";
                                        // fall through — do NOT continue
                                    }
                                    // Iterator / async state position: expose as "_state"
                                    else if (fieldName == "<>1__state")
                                    {
                                        displayName = "_state";
                                        // fall through — do NOT continue
                                    }
                                    // Skip all other compiler infrastructure fields: <>t__builder, <>u__1 etc.
                                    else if (fieldName.StartsWith("<>"))
                                    {
                                        continue;
                                    }
                                    // Existing hoisted variable name extraction (async): <counter>5__2 → "counter"
                                    else if (fieldName.StartsWith("<"))
                                    {
                                        int closeAngle = fieldName.IndexOf('>');
                                        if (closeAngle > 1)
                                            displayName = fieldName.Substring(1, closeAngle - 1);
                                        else
                                            continue; // malformed name — skip
                                    }
                                    // else: plain name (closures) — displayName = fieldName already set above

                                    try
                                    {
                                        objVal.GetFieldValue(cls, fieldToken, out ICorDebugValue fieldVal);
                                        result.Add(VariableReader.ReadValue(displayName, fieldVal));
                                    }
                                    catch { /* field not available at this IL offset */ }
                                }
                                // For MoveNext (async state machine), fields ARE the variables — skip IL locals.
                                // For closure methods (>b__), fields are captured variables but the method also
                                // has its own IL locals (e.g. threadMessage shadowing the captured one) — read both.
                                if (smMethodName == "MoveNext")
                                    readFromStateMachine = true;
                            }
                        }
                    }
                    catch { /* fall through to IL locals */ }
                }

                if (!readFromStateMachine)
                {
                    // Get local variable names from PDB (slot index → name)
                    Dictionary<int, string> localNames = new();
                    try { localNames = PdbReader.GetLocalNames(dllPath, (int)methodToken); }
                    catch { }

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
                }

                // Append static fields from the declaring type of the current method
                try
                {
                    uint declaringTypeToken = PdbReader.GetDeclaringTypeToken(dllPath, (int)methodToken);
                    if (declaringTypeToken != 0)
                    {
                        var staticFieldMap = VariableReader.ReadStaticFieldsFromPE(dllPath, declaringTypeToken);
                        if (staticFieldMap.Count > 0)
                        {
                            module.GetClassFromToken(declaringTypeToken, out ICorDebugClass staticCls);
                            thread.GetActiveFrame(out ICorDebugFrame activeFrame);
                            foreach (var (ft, sfn) in staticFieldMap)
                            {
                                var sv = VariableReader.ReadStaticField(sfn, staticCls, ft, activeFrame);
                                if (!sv.Value.Contains("not available"))
                                    result.Add(sv);
                            }
                        }
                    }
                }
                catch { /* static scan is best-effort */ }

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

                // Try "TypeName.FieldName" static field lookup (highest priority)
                if (expression.Contains('.'))
                {
                    int dotIdx = expression.IndexOf('.');
                    string typePart = expression[..dotIdx];
                    string fieldPart = expression[(dotIdx + 1)..];

                    uint foundTypeToken = PdbReader.FindTypeByName(dllPath, typePart);
                    if (foundTypeToken != 0)
                    {
                        var sfMap = VariableReader.ReadStaticFieldsFromPE(dllPath, foundTypeToken);
                        var matchEntry = sfMap.FirstOrDefault(kv =>
                            kv.Value.Equals(fieldPart, StringComparison.Ordinal));
                        if (matchEntry.Key != 0)
                        {
                            try
                            {
                                module.GetClassFromToken(foundTypeToken, out ICorDebugClass sfCls);
                                thread.GetActiveFrame(out ICorDebugFrame activeFrame2);
                                var sfInfo = VariableReader.ReadStaticField(fieldPart, sfCls, matchEntry.Key, activeFrame2);
                                tcs.SetResult(new EvalResult(true, sfInfo.Value, null));
                                return;
                            }
                            catch (Exception ex)
                            {
                                tcs.SetResult(new EvalResult(false, string.Empty, $"Static field not available: {ex.Message}"));
                                return;
                            }
                        }
                    }
                }

                // Try state machine field lookup first (for async MoveNext methods)
                var (smName, smFields) = PdbReader.GetMethodTypeFields(dllPath, (int)methodToken);
                if (smName == "MoveNext" && smFields.Count > 0)
                {
                    // Search for field matching the expression name (handling <varName>N__M hoisted names)
                    uint? matchedToken = null;
                    foreach (var (ft, fname) in smFields)
                    {
                        if (fname.StartsWith("<>")) continue;
                        string candidateName = fname;
                        if (fname.StartsWith("<"))
                        {
                            int closeAngle = fname.IndexOf('>');
                            if (closeAngle > 1) candidateName = fname.Substring(1, closeAngle - 1);
                            else continue;
                        }
                        if (candidateName.Equals(expression, StringComparison.Ordinal)) { matchedToken = ft; break; }
                    }

                    if (matchedToken.HasValue)
                    {
                        try
                        {
                            ilFrame.GetArgument(0, out ICorDebugValue thisArg);
                            ICorDebugValue actualThis = thisArg;
                            thisArg.GetType(out uint argTypeRaw);
                            var argEt = (CorElementType)argTypeRaw;
                            if (argEt == CorElementType.ByRef || argEt == CorElementType.Class || argEt == CorElementType.Object)
                            {
                                var refVal = (ICorDebugReferenceValue)thisArg;
                                refVal.IsNull(out int isNullThis2);
                                if (isNullThis2 == 0) refVal.Dereference(out actualThis);
                            }
                            if (actualThis is ICorDebugObjectValue objVal2)
                            {
                                objVal2.GetClass(out ICorDebugClass cls2);
                                objVal2.GetFieldValue(cls2, matchedToken.Value, out ICorDebugValue fieldVal2);
                                var varInfo2 = VariableReader.ReadValue(expression, fieldVal2);
                                tcs.SetResult(new EvalResult(true, varInfo2.Value, null));
                                return;
                            }
                        }
                        catch (Exception ex)
                        {
                            tcs.SetResult(new EvalResult(false, string.Empty, $"Field not available: {ex.Message}"));
                            return;
                        }
                    }
                    else
                    {
                        tcs.SetResult(new EvalResult(false, string.Empty,
                            $"Variable '{expression}' not found in current scope"));
                        return;
                    }
                }

                // Fall back to PDB-based IL local lookup
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
