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
    private readonly ChannelWriter<DebugEvent> _events;

    // Set from CreateProcess callback; used by DotnetDebugger for ICorDebugProcess access
    internal ICorDebugProcess? Process { get; private set; }

    // Action invoked when CreateProcess fires — DotnetDebugger sets this before launch
    internal Action<ICorDebugProcess>? OnProcessCreated { get; set; }

    // Action invoked when a module loads — set by DotnetDebugger for pending breakpoint resolution
    internal Action<ICorDebugModule>? OnModuleLoaded { get; set; }

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

    public ManagedCallbackHandler(ChannelWriter<DebugEvent> events)
    {
        _events = events;
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
        // v1 exception callback. Only stop for unhandled exceptions (bUnhandled != 0).
        // First-chance exceptions (bUnhandled == 0) are informational — continue silently.
        // The v2 Exception callback (ICorDebugManagedCallback2) provides richer info and
        // handles the same cases, so we keep v1 minimal to avoid double-reporting.
        if (bUnhandled != 0)
        {
            // STOPPING event for unhandled — do NOT call Continue
            try { pThread.GetID(out uint tid); _events.TryWrite(new ExceptionEvent("<unhandled>", "Unhandled exception", (int)tid, true)); }
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

    public void CreateProcess(ICorDebugProcess pProcess)
    {
        try
        {
            Process = pProcess;
            OnProcessCreated?.Invoke(pProcess);
        }
        finally { pProcess.Continue(0); }
    }

    public void ExitProcess(ICorDebugProcess pProcess)
    {
        // DO NOT call Continue after ExitProcess — process is gone
        _events.TryWrite(new ExitedEvent(0));
        _events.TryComplete();
    }

    public void CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
        => pAppDomain.Continue(0);

    public void ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
        => pAppDomain.Continue(0);

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
            // STOPPING event — do NOT call Continue
            try { pThread.GetID(out uint tid); _events.TryWrite(new ExceptionEvent("<unhandled>", "Unhandled exception", (int)tid, true)); }
            catch { pAppDomain.Continue(0); }
        }
        else
        {
            // First-chance / catch-handler-found: continue silently
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
