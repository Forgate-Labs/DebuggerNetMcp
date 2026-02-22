using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace DebuggerNetMcp.Core.Interop;

// ---------------------------------------------------------------------------
// Enums
// ---------------------------------------------------------------------------

internal enum CorDebugThreadState
{
    THREAD_RUN = 0,
    THREAD_SUSPEND = 1
}

internal enum CorDebugUserState
{
    USER_STOP_REQUESTED = 0x01,
    USER_SUSPEND_REQUESTED = 0x02,
    USER_BACKGROUND = 0x04,
    USER_UNSTARTED = 0x08,
    USER_STOPPED = 0x10,
    USER_WAIT_SLEEP_JOIN = 0x20,
    USER_SUSPENDED = 0x40,
    USER_UNSAFE_POINT = 0x80
}

internal enum CorDebugMappingResult
{
    MAPPING_PROLOG = 0x1,
    MAPPING_EPILOG = 0x2,
    MAPPING_NO_INFO = 0x4,
    MAPPING_UNMAPPED_ADDRESS = 0x8,
    MAPPING_EXACT = 0x10,
    MAPPING_APPROXIMATE = 0x20
}

internal enum CorDebugStepReason
{
    STEP_NORMAL = 0,
    STEP_RETURN = 1,
    STEP_CALL = 2,
    STEP_EXCEPTION_FILTER = 3,
    STEP_EXCEPTION_HANDLER = 4,
    STEP_INTERCEPT = 5,
    STEP_EXIT = 6
}

internal enum CorDebugIntercept
{
    INTERCEPT_NONE = 0x0,
    INTERCEPT_CLASS_INIT = 0x01,
    INTERCEPT_EXCEPTION_FILTER = 0x02,
    INTERCEPT_SECURITY = 0x04,
    INTERCEPT_CONTEXT_POLICY = 0x08,
    INTERCEPT_REMOTING = 0x10,
    INTERCEPT_ALL = 0xFFFF
}

internal enum CorDebugUnmappedStop
{
    STOP_NONE = 0x0,
    STOP_PROLOG = 0x01,
    STOP_EPILOG = 0x02,
    STOP_NO_MAPPING_INFO = 0x04,
    STOP_OTHER_UNMAPPED = 0x08,
    STOP_UNMANAGED = 0x10,
    STOP_ALL = 0xFFFF
}

internal enum CorDebugExceptionCallbackType
{
    DEBUG_EXCEPTION_FIRST_CHANCE = 1,
    DEBUG_EXCEPTION_USER_FIRST_CHANCE = 2,
    DEBUG_EXCEPTION_CATCH_HANDLER_FOUND = 3,
    DEBUG_EXCEPTION_UNHANDLED = 4
}

internal enum CorDebugExceptionUnwindCallbackType
{
    DEBUG_EXCEPTION_UNWIND_BEGIN = 1,
    DEBUG_EXCEPTION_INTERCEPTED = 2
}

// ---------------------------------------------------------------------------
// Structs
// ---------------------------------------------------------------------------

internal struct COR_DEBUG_STEP_RANGE
{
    public uint startOffset;
    public uint endOffset;
}

// ---------------------------------------------------------------------------
// Core interfaces — GUIDs from cordebug.idl
// ---------------------------------------------------------------------------

[GeneratedComInterface]
[Guid("3D6F5F61-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebug
{
    void Initialize();
    void Terminate();
    void SetManagedHandler(ICorDebugManagedCallback pCallback);
    void SetUnmanagedHandler(ICorDebugUnmanagedCallback pCallback);
    void CreateProcess(
        [MarshalAs(UnmanagedType.LPWStr)] string lpApplicationName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        int bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        IntPtr lpStartupInfo,
        IntPtr lpProcessInformation,
        int debuggingFlags,
        out ICorDebugProcess ppProcess);
    void DebugActiveProcess(uint id, int win32Attach, out ICorDebugProcess ppProcess);
    void EnumerateProcesses(out ICorDebugProcessEnum ppProcess);
    void GetProcess(uint dwProcessId, out ICorDebugProcess ppProcess);
    void CanLaunchOrAttach(uint dwProcessId, int win32DebuggingEnabled);
}

[GeneratedComInterface]
[Guid("3D6F5F62-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebugController
{
    void Stop(uint dwTimeoutIgnored);
    void Continue(int fIsOutOfBand);
    void IsRunning(out int pbRunning);
    void HasQueuedCallbacks(ICorDebugThread? pThread, out int pbQueued);
    void EnumerateThreads(out ICorDebugThreadEnum ppThreads);
    void SetAllThreadsDebugState(CorDebugThreadState state, ICorDebugThread? pExceptThisThread);
    void Detach();
    void Terminate(uint exitCode);
    void CanCommitChanges(uint cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError);
    void CommitChanges(uint cSnapshots, ref ICorDebugEditAndContinueSnapshot pSnapshots, out ICorDebugErrorInfoEnum pError);
}

[GeneratedComInterface]
[Guid("3D6F5F64-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebugProcess : ICorDebugController
{
    void GetID(out uint pdwProcessId);
    void GetHandle(out IntPtr phProcessHandle);
    void GetThread(uint dwThreadId, out ICorDebugThread ppThread);
    void EnumerateObjects(out ICorDebugObjectEnum ppObjects);
    void IsTransitionStub(ulong address, out int pbTransitionStub);
    void IsOSSuspended(uint threadId, out int pbSuspended);
    void GetThreadContext(uint threadId, uint contextSize, IntPtr context);
    void SetThreadContext(uint threadId, uint contextSize, IntPtr context);
    void ReadMemory(ulong address, uint size, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] buffer, out nuint read);
    void WriteMemory(ulong address, uint size, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] byte[] buffer, out nuint written);
    void ClearCurrentException(uint threadId);
    void EnableLogMessages(int fOnOff);
    void ModifyLogSwitch([MarshalAs(UnmanagedType.LPWStr)] string pLogSwitchName, int lLevel);
    void EnumerateAppDomains(out ICorDebugAppDomainEnum ppAppDomains);
    void GetObject(out ICorDebugValue ppObject);
    void ThreadForFiberCookie(uint fiberCookie, out ICorDebugThread ppThread);
    void GetHelperThreadID(out uint pThreadID);
}

[GeneratedComInterface]
[Guid("938C6D66-7FB6-4F69-B389-425B8987329B")]
internal partial interface ICorDebugThread
{
    void GetProcess(out ICorDebugProcess ppProcess);
    void GetID(out uint pdwThreadId);
    void GetHandle(out IntPtr phThreadHandle);
    void GetAppDomain(out ICorDebugAppDomain ppAppDomain);
    void SetDebugState(CorDebugThreadState state);
    void GetDebugState(out CorDebugThreadState pState);
    void GetUserState(out CorDebugUserState pState);
    void GetCurrentException(out ICorDebugValue ppExceptionObject);
    void ClearCurrentException();
    void CreateStepper(out ICorDebugStepper ppStepper);
    void EnumerateChains(out ICorDebugChainEnum ppChains);
    void GetActiveChain(out ICorDebugChain ppChain);
    void GetActiveFrame(out ICorDebugFrame ppFrame);
    void GetRegisterSet(out ICorDebugRegisterSet ppRegisters);
    void CreateEval(out ICorDebugEval ppEval);
    void GetObject(out ICorDebugValue ppObject);
}

[GeneratedComInterface]
[Guid("CC7BCAEF-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugFrame
{
    void GetChain(out ICorDebugChain ppChain);
    void GetCode(out ICorDebugCode ppCode);
    void GetFunction(out ICorDebugFunction ppFunction);
    void GetFunctionToken(out uint pToken);
    void GetStackRange(out ulong pStart, out ulong pEnd);
    void GetCaller(out ICorDebugFrame ppFrame);
    void GetCallee(out ICorDebugFrame ppCallee);
    void CreateStepper(out ICorDebugStepper ppStepper);
}

[GeneratedComInterface]
[Guid("03E26311-4F76-11D3-88C6-006097945418")]
internal partial interface ICorDebugILFrame : ICorDebugFrame
{
    void GetIP(out uint pnOffset, out CorDebugMappingResult pMappingResult);
    void SetIP(uint nOffset);
    void EnumerateLocalVariables(out ICorDebugValueEnum ppValueEnum);
    void GetLocalVariable(uint dwIndex, out ICorDebugValue ppValue);
    void EnumerateArguments(out ICorDebugValueEnum ppValueEnum);
    void GetArgument(uint dwIndex, out ICorDebugValue ppValue);
    void GetStackDepth(out uint pDepth);
    void GetStackValue(uint dwIndex, out ICorDebugValue ppValue);
    void CanSetIP(uint nOffset);
}

[GeneratedComInterface]
[Guid("CC7BCAF3-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugFunction
{
    void GetModule(out ICorDebugModule ppModule);
    void GetClass(out ICorDebugClass ppClass);
    void GetToken(out uint pMethodDef);
    void GetILCode(out ICorDebugCode ppCode);
    void GetNativeCode(out ICorDebugCode ppCode);
    void CreateBreakpoint(out ICorDebugFunctionBreakpoint ppBreakpoint);
    void GetLocalVarSigToken(out uint pmdSig);
    void GetCurrentVersionNumber(out uint pnCurrentVersion);
    void GetVersionNumber(out uint pnVersion);
}

[GeneratedComInterface]
[Guid("DBA2D8C1-E5C5-4069-8C13-10A7C6ABF43D")]
internal partial interface ICorDebugModule
{
    void GetProcess(out ICorDebugProcess ppProcess);
    void GetBaseAddress(out ulong pAddress);
    void GetAssembly(out ICorDebugAssembly ppAssembly);
    void GetName(uint cchName, out uint pcchName, IntPtr szName);
    void EnableJITDebugging(int bTrackJITInfo, int bAllowJitOpts);
    void EnableClassLoadCallbacks(int bClassLoadCallbacks);
    void GetFunctionFromToken(uint methodDef, out ICorDebugFunction ppFunction);
    void GetFunctionFromRVA(ulong rva, out ICorDebugFunction ppFunction);
    void GetClassFromToken(uint typeDef, out ICorDebugClass ppClass);
    void CreateBreakpoint(out ICorDebugModuleBreakpoint ppBreakpoint);
    void GetEditAndContinueSnapshot(out ICorDebugEditAndContinueSnapshot ppEditAndContinueSnapshot);
    void GetMetaDataInterface(in Guid riid, out IntPtr ppObj);
    void GetToken(out uint pToken);
    void IsDynamic(out int pbDynamic);
    void GetGlobalVariableValue(uint fieldDef, out ICorDebugValue ppValue);
    void GetSize(out uint pcBytes);
    void IsInMemory(out int pbInMemory);
}

[GeneratedComInterface]
[Guid("CC7BCAF7-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugValue
{
    void GetType(out uint pType);
    void GetSize(out uint pSize);
    void GetAddress(out ulong pAddress);
    void CreateBreakpoint(out ICorDebugValueBreakpoint ppBreakpoint);
}

[GeneratedComInterface]
[Guid("CC7BCAFA-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugHeapValue : ICorDebugValue
{
    void IsValid(out int pbValid);
    void CreateRelocBreakpoint(out ICorDebugValueBreakpoint ppBreakpoint);
}

[GeneratedComInterface]
[Guid("CC7BCAF8-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugGenericValue : ICorDebugValue
{
    void GetValue([MarshalAs(UnmanagedType.LPArray)] byte[] pTo);
    void SetValue([MarshalAs(UnmanagedType.LPArray)] byte[] pFrom);
}

[GeneratedComInterface]
[Guid("CC7BCAF9-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugReferenceValue : ICorDebugValue
{
    void IsNull(out int pbNull);
    void GetValue(out ulong pValue);
    void SetValue(ulong value);
    void Dereference(out ICorDebugValue ppValue);
    void DereferenceStrong(out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("CC7BCAFD-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugStringValue : ICorDebugHeapValue
{
    void GetLength(out uint pcchString);
    void GetString(uint cchString, out uint pcchString, IntPtr szString);
}

[GeneratedComInterface]
[Guid("18AD3D6E-B7D2-11D2-BD04-0000F80849BD")]
internal partial interface ICorDebugObjectValue : ICorDebugValue
{
    void GetClass(out ICorDebugClass ppClass);
    void GetFieldValue(ICorDebugClass pClass, uint fieldDef, out ICorDebugValue ppValue);
    void GetVirtualMethod(uint memberRef, out ICorDebugFunction ppFunction);
    void GetContext(out ICorDebugContext ppContext);
    void IsValueClass(out int pbIsValueClass);
    void GetManagedCopy(out IntPtr ppObject);
    void SetFromManagedCopy(IntPtr pObject);
}

[GeneratedComInterface]
[Guid("0405B0DF-A660-11D2-BD02-0000F80849BD")]
internal partial interface ICorDebugArrayValue : ICorDebugHeapValue
{
    void GetElementType(out uint pType);
    void GetRank(out uint pnRank);
    void GetCount(out uint pnCount);
    void GetDimensions(uint cdim, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] dims);
    void HasBaseIndicies(out int pbHasBaseIndicies);
    void GetBaseIndicies(uint cdim, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] indicies);
    void GetElement(uint cdim, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 0)] uint[] indices, out ICorDebugValue ppValue);
    void GetElementAtPosition(uint nPosition, out ICorDebugValue ppValue);
}

[GeneratedComInterface]
[Guid("CC7BCAE8-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugBreakpoint
{
    void Activate(int bActive);
    void IsActive(out int pbActive);
}

[GeneratedComInterface]
[Guid("CC7BCAE9-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugFunctionBreakpoint : ICorDebugBreakpoint
{
    void GetFunction(out ICorDebugFunction ppFunction);
    void GetOffset(out uint pnOffset);
}

[GeneratedComInterface]
[Guid("CC7BCAEC-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugStepper
{
    void IsActive(out int pbActive);
    void Deactivate();
    void SetInterceptMask(CorDebugIntercept mask);
    void SetUnmappedStopMask(CorDebugUnmappedStop mask);
    void Step(int bStepIn);
    void StepRange(int bStepIn, [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] COR_DEBUG_STEP_RANGE[] ranges, uint cRangeCount);
    void StepOut();
    void SetRangeIL(int bIL);
}

[GeneratedComInterface]
[Guid("3D6F5F60-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebugManagedCallback
{
    void Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint);
    void StepComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason);
    void Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread);
    void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int bUnhandled);
    void EvalComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval);
    void EvalException(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugEval pEval);
    void CreateProcess(ICorDebugProcess pProcess);
    void ExitProcess(ICorDebugProcess pProcess);
    void CreateThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread);
    void ExitThread(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread);
    void LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule);
    void UnloadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule);
    void LoadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c);
    void UnloadClass(ICorDebugAppDomain pAppDomain, ICorDebugClass c);
    void DebuggerError(ICorDebugProcess pProcess, int errorHR, uint errorCode);
    void LogMessage(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, [MarshalAs(UnmanagedType.LPWStr)] string pLogSwitchName, [MarshalAs(UnmanagedType.LPWStr)] string pMessage);
    void LogSwitch(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int lLevel, int ulReason, [MarshalAs(UnmanagedType.LPWStr)] string pLogSwitchName, [MarshalAs(UnmanagedType.LPWStr)] string pParentName);
    void CreateAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain);
    void ExitAppDomain(ICorDebugProcess pProcess, ICorDebugAppDomain pAppDomain);
    void LoadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly);
    void UnloadAssembly(ICorDebugAppDomain pAppDomain, ICorDebugAssembly pAssembly);
    void ControlCTrap(ICorDebugProcess pProcess);
    void NameChange(ICorDebugAppDomain? pAppDomain, ICorDebugThread? pThread);
    void UpdateModuleSymbols(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule, IntPtr pSymbolStream);
    void EditAndContinueRemap(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction, int fAccurate);
    void BreakpointSetError(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint, uint dwError);
}

[GeneratedComInterface]
[Guid("250E5EEA-DB5C-4C76-B6F3-8C46F12E3203")]
internal partial interface ICorDebugManagedCallback2
{
    void FunctionRemapOpportunity(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pOldFunction, ICorDebugFunction pNewFunction, uint oldILOffset);
    void CreateConnection(ICorDebugProcess pProcess, uint dwConnectionId, [MarshalAs(UnmanagedType.LPWStr)] ref string pConnName);
    void ChangeConnection(ICorDebugProcess pProcess, uint dwConnectionId);
    void DestroyConnection(ICorDebugProcess pProcess, uint dwConnectionId);
    void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFrame? pFrame, uint nOffset, CorDebugExceptionCallbackType dwEventType, uint dwFlags);
    void ExceptionUnwind(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, CorDebugExceptionUnwindCallbackType dwEventType, uint dwFlags);
    void FunctionRemapComplete(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugFunction pFunction);
    void MDANotification(ICorDebugController pController, ICorDebugThread pThread, ICorDebugMDA pMDA);
}

// ---------------------------------------------------------------------------
// Stub interfaces — GUIDs from cordebug.idl
// ---------------------------------------------------------------------------

[GeneratedComInterface]
[Guid("3D6F5F63-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebugAppDomain : ICorDebugController
{
}

[GeneratedComInterface]
[Guid("DF59507C-D47A-459E-BCE2-6427EAC8FD06")]
internal partial interface ICorDebugAssembly
{
}

[GeneratedComInterface]
[Guid("CC7BCAEE-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugChain
{
}

[GeneratedComInterface]
[Guid("CC7BCAF5-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugClass
{
    void GetModule(out ICorDebugModule pModule);
    void GetToken(out uint pTypeDef);
}

[GeneratedComInterface]
[Guid("CC7BCB00-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugContext : ICorDebugObjectValue
{
}

[GeneratedComInterface]
[Guid("CC7BCAF4-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugCode
{
}

[GeneratedComInterface]
[Guid("CC7BCAF6-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugEval
{
}

[GeneratedComInterface]
[Guid("CC726F2F-1DB7-459B-B0EC-05F01D841B42")]
internal partial interface ICorDebugMDA
{
}

[GeneratedComInterface]
[Guid("CC7BCB0B-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugRegisterSet
{
}

[GeneratedComInterface]
[Guid("F0E18809-72B5-11D2-976F-00A0C9B4D50C")]
internal partial interface ICorDebugErrorInfoEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCB02-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugObjectEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCB05-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugProcessEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCB06-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugThreadEnum
{
}

[GeneratedComInterface]
[Guid("63CA1B24-4359-4883-BD57-13F815F58744")]
internal partial interface ICorDebugAppDomainEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCB08-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugChainEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCB0A-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugValueEnum
{
}

[GeneratedComInterface]
[Guid("CC7BCAEA-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugModuleBreakpoint : ICorDebugBreakpoint
{
}

[GeneratedComInterface]
[Guid("CC7BCAEB-8A68-11D2-983C-0000F808342D")]
internal partial interface ICorDebugValueBreakpoint : ICorDebugBreakpoint
{
}

[GeneratedComInterface]
[Guid("6DC3FA01-D7CB-11D2-8A95-0080C792E5D8")]
internal partial interface ICorDebugEditAndContinueSnapshot
{
}

[GeneratedComInterface]
[Guid("5263E909-8CB5-11D3-BD2F-0000F80849BD")]
internal partial interface ICorDebugUnmanagedCallback
{
}
