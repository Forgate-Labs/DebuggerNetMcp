using System.Collections.Generic;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Channels;
using DebuggerNetMcp.Core.Interop;

namespace DebuggerNetMcp.Core.Engine;

/// <summary>
/// COM callback sink for all ICorDebug debug events.
/// Implements ICorDebugManagedCallback (26 methods) and ICorDebugManagedCallback2 (8 methods).
///
/// Thread model: ICorDebug delivers callbacks on its own internal thread.
/// INVARIANT for informational events (module load, thread create, etc.): MUST call
/// pAppDomain.Continue(0) before returning or the process freezes.
/// EXCEPTION — stopping events (Breakpoint, StepComplete, Break): must NOT call Continue;
/// the process stays stopped so the caller can inspect state. DotnetDebugger.ContinueAsync
/// calls Continue when the user issues debug_continue / debug_step_*.
/// ExitProcess: must NOT call Continue (process is gone).
/// </summary>
[GeneratedComClass]
internal sealed partial class ManagedCallbackHandler
    : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    private ChannelWriter<DebugEvent> _events;

    // Set from CreateProcess callback; used by DotnetDebugger for ICorDebugProcess access
    internal ICorDebugProcess? Process { get; private set; }

    // Action invoked when CreateProcess fires — DotnetDebugger sets this before launch
    internal Action<ICorDebugProcess>? OnProcessCreated { get; set; }

    // Action invoked when a module loads — set by DotnetDebugger for pending breakpoint resolution
    internal Action<ICorDebugModule>? OnModuleLoaded { get; set; }

    // When true, CreateProcess does NOT call Continue and emits a StoppedEvent("process_created").
    // Set by DotnetDebugger before launch so the caller can configure breakpoints before execution.
    internal bool StopAtCreateProcess { get; set; }

    // Maps ICorDebugFunctionBreakpoint wrapper object reference → breakpoint ID for hit reporting.
    // Source-generated COM interfaces wrap native pointers in managed proxy objects; identity
    // comparison (ReferenceEquals) is NOT reliable across callback boundaries. We use the
    // breakpoint's function token as a stable key instead.
    // Key: methodDef token (uint), Value: breakpoint ID assigned by DotnetDebugger.
    internal Dictionary<uint, int> BreakpointTokenToId { get; } = new();

    // Thread ID of the last stopping event (Breakpoint, StepComplete, Break).
    // Used by DotnetDebugger.GetCurrentThread() to call GetThread(id) directly,
    // avoiding ICorDebugThreadEnum.Next() which has LPArray marshaling issues.
    internal uint CurrentStoppedThreadId { get; private set; }

    // All live managed thread IDs, tracked via CreateThread/ExitThread callbacks.
    // ICorDebugProcess.EnumerateThreads() has COM interop issues on Linux (source-generated wrappers);
    // tracking via callbacks is the reliable alternative.
    private readonly HashSet<uint> _knownThreadIds = new();
    internal IReadOnlyCollection<uint> KnownThreadIds => _knownThreadIds;

    internal void ClearKnownThreadIds() => _knownThreadIds.Clear();

    internal void ClearBreakpointRegistry() => BreakpointTokenToId.Clear();

    // Controls whether first-chance exceptions produce a stopping ExceptionEvent.
    // Set by DotnetDebugger.LaunchAsync before launching. Default false (continue silently).
    internal bool NotifyFirstChanceExceptions { get; set; }

    // Set by v1 Exception callback when it writes a stopping unhandled ExceptionEvent.
    // Prevents the v2 Exception callback from writing a duplicate stopping event.
    private bool _exceptionStopPending;

    public ManagedCallbackHandler(ChannelWriter<DebugEvent> events)
    {
        _events = events;
    }

    /// <summary>
    /// Replaces the event writer with a fresh channel writer for a new debug session.
    /// Called by DotnetDebugger when relaunching after a previous session completed.
    /// </summary>
    internal void UpdateEventWriter(ChannelWriter<DebugEvent> events)
    {
        _events = events;
    }

    /// <summary>
    /// When true, ExitProcess suppresses TryComplete on the event channel.
    /// Set before forcibly terminating a previous session so the new session's channel is not closed.
    /// </summary>
    internal bool SuppressExitProcess { get; set; }

    // Session ID incremented each time a new debug session starts (LaunchAsync / AttachAsync).
    // ExitProcess callback captures the current session ID; if it has changed by the time
    // ExitProcess fires, the callback belongs to a stale session and does not close the new channel.
    private int _currentSessionId;
    internal int CurrentSessionId => _currentSessionId;

    internal void BeginNewSession()
    {
        System.Threading.Interlocked.Increment(ref _currentSessionId);
    }

    // -----------------------------------------------------------------------
    // ICorDebugManagedCallback — 26 methods
    // All MUST end with pAppDomain.Continue(0) (or pProcess.Continue(0))
    // -----------------------------------------------------------------------

    public void Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugBreakpoint pBreakpoint)
    {
        // STOPPING event — do NOT call Continue. Process stays stopped so the caller can
        // inspect locals/stack. DotnetDebugger.ContinueAsync resumes when ready.
        pThread.GetID(out uint tid);
        CurrentStoppedThreadId = tid;
        int bpId = -1;
        if (pBreakpoint is ICorDebugFunctionBreakpoint fbp)
        {
            try
            {
                fbp.GetFunction(out ICorDebugFunction fn);
                fn.GetToken(out uint token);
                BreakpointTokenToId.TryGetValue(token, out bpId);
            }
            catch { }
        }
        var frame = TryGetTopFrame(pThread);
        _events.TryWrite(bpId >= 0
            ? new BreakpointHitEvent(bpId, (int)tid, frame ?? new StackFrameInfo(0, "<unknown>", null, null, 0))
            : new StoppedEvent("breakpoint", (int)tid, frame));
    }

    public void StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugStepper pStepper, CorDebugStepReason reason)
    {
        // STOPPING event — do NOT call Continue.
        pThread.GetID(out uint tid);
        CurrentStoppedThreadId = tid;
        var frame = TryGetTopFrame(pThread);
        _events.TryWrite(new StoppedEvent("step", (int)tid, frame));
    }

    public void Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        // STOPPING event — do NOT call Continue.
        pThread.GetID(out uint tid);
        CurrentStoppedThreadId = tid;
        _events.TryWrite(new StoppedEvent("pause", (int)tid, null));
    }

    public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int bUnhandled)
    {
        if (bUnhandled != 0)
        {
            // Second-chance (unhandled) — STOPPING event. Do NOT call Continue.
            try
            {
                pThread.GetID(out uint tid);
                CurrentStoppedThreadId = tid;
                var (exType, exMsg) = TryReadExceptionInfo(pThread);
                _exceptionStopPending = true;
                _events.TryWrite(new ExceptionEvent(exType, exMsg, (int)tid, true));
            }
            catch { pAppDomain.Continue(0); }
        }
        else if (NotifyFirstChanceExceptions)
        {
            // First-chance with notifications enabled — STOPPING event. Do NOT call Continue.
            try
            {
                pThread.GetID(out uint tid);
                CurrentStoppedThreadId = tid;
                var (exType, exMsg) = TryReadExceptionInfo(pThread);
                _events.TryWrite(new ExceptionEvent(exType, exMsg, (int)tid, false));
            }
            catch { pAppDomain.Continue(0); }
        }
        else
        {
            pAppDomain.Continue(0);  // first-chance: continue silently
        }
    }

    public void EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugEval pEval)
        => pAppDomain.Continue(0);

    public void EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugEval pEval)
        => pAppDomain.Continue(0);

    // Session ID captured at CreateProcess — compared in ExitProcess to detect stale callbacks.
    private int _processSessionId;

    public void CreateProcess(ICorDebugProcess pProcess)
    {
        // Capture the session ID at process creation so ExitProcess can validate it later.
        _processSessionId = _currentSessionId;
        Process = pProcess;
        OnProcessCreated?.Invoke(pProcess);

        if (StopAtCreateProcess)
        {
            // STOPPING: do NOT call Continue — process is paused so the caller can set breakpoints.
            // LaunchAsync is waiting for this event on the event channel.
            StopAtCreateProcess = false;
            _events.TryWrite(new StoppedEvent("process_created", 0, null));
        }
        else
        {
            pProcess.Continue(0);
        }
    }

    public void ExitProcess(ICorDebugProcess pProcess)
    {
        // DO NOT call Continue after ExitProcess — process is gone.
        // If SuppressExitProcess is set, this callback is from a process terminated by DisconnectAsync
        // during a new launch; suppress TryComplete so the new session's channel is not closed.
        if (SuppressExitProcess)
        {
            SuppressExitProcess = false;
            return;
        }
        // Session ID guard: if the session has advanced since CreateProcess, this ExitProcess
        // belongs to a stale session (e.g., old attach session firing after a new LaunchTestAsync).
        // Suppress channel completion to prevent closing the new session's event channel.
        if (_processSessionId != _currentSessionId)
            return;
        _events.TryWrite(new ExitedEvent(0));
        _events.TryComplete();
    }

    public void CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        try { pThread.GetID(out uint tid); _knownThreadIds.Add(tid); } catch { }
        pAppDomain.Continue(0);
    }

    public void ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        try { pThread.GetID(out uint tid); _knownThreadIds.Remove(tid); } catch { }
        pAppDomain.Continue(0);
    }

    public void LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
    {
        try
        {
            OnModuleLoaded?.Invoke(pModule);
        }
        finally { pAppDomain.Continue(0); }
    }

    public void UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
        => pAppDomain.Continue(0);

    public void LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
        => pAppDomain.Continue(0);

    public void UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c)
        => pAppDomain.Continue(0);

    public void DebuggerError(ICorDebugProcess pProcess, int errorHR, uint errorCode)
    {
        _events.TryWrite(new ExceptionEvent("DebuggerError",
            $"HRESULT 0x{errorHR:X8} code {errorCode}", 0, true));
        try { pProcess.Continue(0); } catch { /* ignore if process is in error state */ }
    }

    public void LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        int lLevel, string pLogSwitchName, string pMessage)
        => pAppDomain.Continue(0);

    public void LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        int lLevel, int ulReason, string pLogSwitchName, string pParentName)
        => pAppDomain.Continue(0);

    public void CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
        => pAppDomain.Continue(0);

    public void ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain)
        => pAppDomain.Continue(0);

    public void LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
        => pAppDomain.Continue(0);

    public void UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly)
        => pAppDomain.Continue(0);

    public void ControlCTrap(ICorDebugProcess pProcess)
    {
        try { pProcess.Continue(0); } catch { /* ignore */ }
    }

    public void NameChange(ICorDebugAppDomain? pAppDomain, ICorDebugThread? pThread)
    {
        try { pAppDomain?.Continue(0); } catch { /* ignore */ }
    }

    public void UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule,
        IntPtr pSymbolStream)
        => pAppDomain.Continue(0);

    public void EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugFunction pFunction, int fAccurate)
        => pAppDomain.Continue(0);

    public void BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugBreakpoint pBreakpoint, uint dwError)
        => pAppDomain.Continue(0);

    // -----------------------------------------------------------------------
    // ICorDebugManagedCallback2 — 8 methods
    // All MUST end with pAppDomain.Continue(0) or pProcess.Continue(0)
    // -----------------------------------------------------------------------

    public void FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain,
        ICorDebugThread pThread, ICorDebugFunction pOldFunction,
        ICorDebugFunction pNewFunction, uint oldILOffset)
        => pAppDomain.Continue(0);

    public void CreateConnection(ICorDebugProcess pProcess, uint dwConnectionId,
        ref string pConnName)
    {
        try { pProcess.Continue(0); } catch { /* ignore */ }
    }

    public void ChangeConnection(ICorDebugProcess pProcess, uint dwConnectionId)
    {
        try { pProcess.Continue(0); } catch { /* ignore */ }
    }

    public void DestroyConnection(ICorDebugProcess pProcess, uint dwConnectionId)
    {
        try { pProcess.Continue(0); } catch { /* ignore */ }
    }

    public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugFrame? pFrame, uint nOffset,
        CorDebugExceptionCallbackType dwEventType, uint dwFlags)
    {
        if (dwEventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED)
        {
            if (_exceptionStopPending)
            {
                // v1 callback already handled this unhandled exception and wrote the stopping event.
                // Continue silently to avoid double-stopping and double-writing to the event channel.
                _exceptionStopPending = false;
                pAppDomain.Continue(0);
            }
            else
            {
                // v1 did not fire (should not happen on .NET 10) — handle here as fallback.
                try
                {
                    pThread.GetID(out uint tid);
                    CurrentStoppedThreadId = tid;
                    var (exType, exMsg) = TryReadExceptionInfo(pThread);
                    _events.TryWrite(new ExceptionEvent(exType, exMsg, (int)tid, true));
                }
                catch { pAppDomain.Continue(0); }
            }
        }
        else
        {
            // First-chance / catch-handler-found: continue silently.
            // First-chance notifications are handled in the v1 callback.
            pAppDomain.Continue(0);
        }
    }

    public void ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        CorDebugExceptionUnwindCallbackType dwEventType, uint dwFlags)
        => pAppDomain.Continue(0);

    public void FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugFunction pFunction)
        => pAppDomain.Continue(0);

    public void MDANotification(ICorDebugController pController, ICorDebugThread pThread,
        ICorDebugMDA pMDA)
    {
        try { pController.Continue(0); } catch { /* ignore */ }
    }

    // -----------------------------------------------------------------------
    // Helper: read exception type and message from the thread's current exception
    // -----------------------------------------------------------------------

    /// <summary>
    /// Reads the exception type name and message from the thread's current exception.
    /// Must be called while stopped in an exception callback (before any Continue call).
    /// </summary>
    private static (string typeName, string message) TryReadExceptionInfo(ICorDebugThread pThread)
    {
        try
        {
            pThread.GetCurrentException(out ICorDebugValue exVal);
            if (exVal == null) return ("<unknown>", "No exception available");

            // Dereference the reference value to get the actual exception object
            ICorDebugValue actual = exVal;
            if (exVal is ICorDebugReferenceValue rv)
            {
                rv.IsNull(out int isNull);
                if (isNull != 0) return ("<unknown>", "Exception reference is null");
                rv.Dereference(out ICorDebugValue inner);
                actual = inner;
            }

            string typeName = "<unknown>";
            string message = "Unhandled exception";

            if (actual is ICorDebugObjectValue objVal)
            {
                objVal.GetClass(out ICorDebugClass cls);
                cls.GetModule(out ICorDebugModule module);
                cls.GetToken(out uint typedefToken);
                string dllPath = VariableReader.GetModulePath(module);

                typeName = VariableReader.GetTypeName(dllPath, typedefToken);
                if (string.IsNullOrEmpty(typeName) || typeName == "object")
                    typeName = "<unknown>";

                // Read _message field — System.Exception stores message in private field _message.
                // Walk inheritance chain; pass module so GetClassFromToken picks the correct
                // ICorDebugClass for each level (required by ICorDebugObjectValue.GetFieldValue).
                string? found = TryReadStringField(objVal, module, dllPath, typedefToken, "_message");
                if (found != null) message = found;
            }

            return (typeName, message);
        }
        catch { return ("<unknown>", "Exception info unavailable"); }
    }

    /// <summary>
    /// Reads a single string field from an exception object by walking the inheritance chain.
    /// Returns null if the field is not found or cannot be read.
    /// </summary>
    private static string? TryReadStringField(
        ICorDebugObjectValue objVal, ICorDebugModule module, string dllPath, uint typedefToken, string fieldName)
    {
        uint current = typedefToken;
        while (current != 0)
        {
            var fields = VariableReader.ReadInstanceFieldsFromPE(dllPath, current);
            uint targetRid = 0;
            foreach (var (rid, name) in fields)
            {
                if (name == fieldName) { targetRid = rid; break; }
            }
            if (targetRid != 0)
            {
                try
                {
                    // Use the class at this inheritance level, not the runtime (derived) class.
                    // ICorDebugObjectValue.GetFieldValue requires the declaring class's ICorDebugClass.
                    module.GetClassFromToken(current, out ICorDebugClass cls);
                    objVal.GetFieldValue(cls, targetRid, out ICorDebugValue fieldVal);
                    if (fieldVal is ICorDebugReferenceValue rv2)
                    {
                        rv2.IsNull(out int isNull);
                        if (isNull != 0) return null;
                        rv2.Dereference(out ICorDebugValue inner2);
                        if (inner2 is ICorDebugStringValue sv)
                        {
                            sv.GetLength(out uint len);
                            if (len == 0) return string.Empty;
                            IntPtr buf = System.Runtime.InteropServices.Marshal.AllocHGlobal((int)(len + 1) * 2);
                            try
                            {
                                sv.GetString(len, out _, buf);
                                return System.Runtime.InteropServices.Marshal.PtrToStringUni(buf, (int)len);
                            }
                            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(buf); }
                        }
                    }
                }
                catch { /* field not readable in this frame */ }
                return null;
            }
            current = VariableReader.GetBaseTypeToken(dllPath, current);
        }
        return null;
    }

    // -----------------------------------------------------------------------
    // Helper: get the top managed IL frame for StoppedEvent / BreakpointHitEvent
    // -----------------------------------------------------------------------
    private static StackFrameInfo? TryGetTopFrame(ICorDebugThread pThread)
    {
        try
        {
            pThread.GetActiveFrame(out ICorDebugFrame frame);
            if (frame is ICorDebugILFrame ilFrame)
            {
                ilFrame.GetIP(out uint ip, out _);
                frame.GetFunction(out ICorDebugFunction fn);
                fn.GetToken(out uint token);
                return new StackFrameInfo(0, $"0x{token:X8}", null, null, (int)ip);
            }
        }
        catch { /* native frame or prolog — no IL frame available */ }
        return null;
    }
}
