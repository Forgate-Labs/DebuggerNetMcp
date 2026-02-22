using System.Runtime.InteropServices.Marshalling;
using System.Threading.Channels;
using DebuggerNetMcp.Core.Interop;

namespace DebuggerNetMcp.Core.Engine;

/// <summary>
/// COM callback sink for all ICorDebug debug events.
/// Implements ICorDebugManagedCallback (26 methods) and ICorDebugManagedCallback2 (8 methods).
///
/// INVARIANT: Every method in ICorDebugManagedCallback MUST call pAppDomain.Continue(0)
/// before returning, or the debuggee process will freeze permanently.
/// ExitProcess is the sole exception — Continue must NOT be called after the process exits.
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
        try
        {
            pThread.GetID(out uint tid);
            int bpId = -1;
            if (pBreakpoint is ICorDebugFunctionBreakpoint fbp)
            {
                // Use the method token as the stable identity key for the breakpoint.
                // DotnetDebugger stores the same token in BreakpointTokenToId when setting
                // the breakpoint via ICorDebugFunction.CreateBreakpoint.
                try
                {
                    fbp.GetFunction(out ICorDebugFunction fn);
                    fn.GetToken(out uint token);
                    BreakpointTokenToId.TryGetValue(token, out bpId);
                }
                catch { /* if token lookup fails, fall through to generic StoppedEvent */ }
            }
            var frame = TryGetTopFrame(pThread);
            _events.TryWrite(bpId >= 0
                ? new BreakpointHitEvent(bpId, (int)tid, frame ?? new StackFrameInfo(0, "<unknown>", null, null, 0))
                : new StoppedEvent("breakpoint", (int)tid, frame));
        }
        finally { pAppDomain.Continue(0); }
    }

    public void StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
        ICorDebugStepper pStepper, CorDebugStepReason reason)
    {
        try
        {
            pThread.GetID(out uint tid);
            var frame = TryGetTopFrame(pThread);
            _events.TryWrite(new StoppedEvent("step", (int)tid, frame));
        }
        finally { pAppDomain.Continue(0); }
    }

    public void Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
    {
        try
        {
            pThread.GetID(out uint tid);
            _events.TryWrite(new StoppedEvent("pause", (int)tid, null));
        }
        finally { pAppDomain.Continue(0); }
    }

    public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int bUnhandled)
    {
        try
        {
            pThread.GetID(out uint tid);
            _events.TryWrite(new ExceptionEvent("<unknown>", "<exception>", (int)tid, bUnhandled != 0));
        }
        finally { pAppDomain.Continue(0); }
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
        try
        {
            if (dwEventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED ||
                dwEventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_FIRST_CHANCE)
            {
                pThread.GetID(out uint tid);
                bool isUnhandled = dwEventType == CorDebugExceptionCallbackType.DEBUG_EXCEPTION_UNHANDLED;
                _events.TryWrite(new ExceptionEvent("<exception>", "<exception occurred>",
                    (int)tid, isUnhandled));
            }
        }
        finally { pAppDomain.Continue(0); }
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
