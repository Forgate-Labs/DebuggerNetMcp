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
#pragma warning disable CS0414 // Field assigned but value never used — consumed in Plan 04
    private int _nextBreakpointId = 1;
#pragma warning restore CS0414

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

    // ResolveBreakpoint stub — implemented in Plan 04
    private void ResolveBreakpoint(ICorDebugModule module, int id, int methodToken, int ilOffset)
    {
        throw new NotImplementedException("ResolveBreakpoint will be implemented in Plan 04");
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
    // Private: Pending breakpoint record
    // -----------------------------------------------------------------------

    private record PendingBreakpoint(int Id, string DllName, int MethodToken, int ILOffset);
}
