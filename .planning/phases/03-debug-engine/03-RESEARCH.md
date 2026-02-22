# Phase 3: Debug Engine - Research

**Researched:** 2026-02-22
**Domain:** ICorDebug COM interop on Linux — launch, callback dispatch, breakpoints, stepping, variable inspection
**Confidence:** HIGH (core sequences verified against official docs and authoritative blog posts)

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| ENGINE-04 | DotnetDebugger.cs — LaunchAsync (dotnet build -c Debug + RegisterForRuntimeStartup) and AttachAsync | Launch sequence documented; `CreateProcessForLaunch` + `RegisterForRuntimeStartup` + `ResumeProcess` flow fully verified |
| ENGINE-05 | DotnetDebugger.cs — execution control: ContinueAsync, StepOverAsync, StepIntoAsync, StepOutAsync, PauseAsync | `ICorDebugStepper` usage sequence documented with all three Step variants |
| ENGINE-06 | DotnetDebugger.cs — breakpoints: SetBreakpointAsync(file, line), RemoveBreakpointAsync(id) | `LoadModule` pending-breakpoint pattern documented; `GetFunctionFromToken` + `CreateBreakpoint` flow verified |
| ENGINE-07 | DotnetDebugger.cs — inspection: GetStackTraceAsync, GetLocalsAsync, EvaluateAsync | Frame walking via `GetActiveFrame`/`EnumerateChains`; locals via `ICorDebugILFrame.GetLocalVariable(index)` + metadata count documented |
| ENGINE-08 | Dedicated thread for ICorDebug + `Channel<DebugEvent>` for async communication | `Channel.CreateUnbounded` + dedicated `Thread` pattern documented with GC lifetime and deadlock guards |
</phase_requirements>

---

## Summary

Phase 3 is the highest-risk phase in this project. It wires together all components from Phase 2 into a single `DotnetDebugger.cs` class that can launch a .NET process, receive ICorDebug callbacks on a dedicated thread, hit breakpoints, step, and inspect variables. The core challenge is correctness of ICorDebug protocol: every callback handler MUST call `ICorDebugController.Continue()` or the debuggee process stops permanently. Combined with the kernel 6.12+ SIGSEGV risk, defensive coding of the launch sequence is critical.

The recommended architecture is: one dedicated `Thread` owns all ICorDebug calls and callback dispatch; a `Channel<DebugEvent>` (unbounded, `SingleWriter=true`) bridges that thread to async callers. The `ManagedCallbackHandler` class uses `[GeneratedComClass]` over two `[GeneratedComInterface]`-attributed interfaces (`ICorDebugManagedCallback` and `ICorDebugManagedCallback2`). Breakpoints are resolved at `LoadModule` time — a pending-breakpoints dictionary enables breakpoints set before module load. Object field enumeration requires `ICorDebugModule.GetMetaDataInterface` → `IMetaDataImport` COM wrapper, which is the one nontrivial deferred item from Phase 2.

**Primary recommendation:** Implement `DotnetDebugger.cs` as a single class with one dedicated `Thread`, one `Channel<DebugEvent>`, and one `ManagedCallbackHandler`. All ICorDebug API calls must occur on the dedicated thread. Async public methods write commands to the dedicated thread via a second `Channel<Action>` (command channel), await a `TaskCompletionSource<T>` for the result, and receive the result when the callback posts it. This avoids all deadlock scenarios.

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| `System.Threading.Channels` | In-box .NET 10 | Thread-safe producer/consumer bridge between ICorDebug callback thread and async callers | Native to .NET 3.0+, no NuGet needed, zero-alloc fast path for unbounded write |
| `System.Runtime.InteropServices.Marshalling` | In-box .NET 8+ | `[GeneratedComClass]`, `[GeneratedComInterface]` source generation | Required for `ManagedCallbackHandler` COM registration |
| `System.Reflection.Metadata` | In-box .NET 10 | `IMetaDataImport` via `GetMetaDataInterface` — field/method metadata for object inspection | Already used in PdbReader; consistent approach |

### Supporting
| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| `System.Runtime.InteropServices.Marshal` | In-box | `GetObjectForIUnknown(pCordb)` — convert native ICorDebug pointer to managed interface | Only in `RuntimeStartupCallback` to unwrap the `IntPtr pCordb` |

### Alternatives Considered
| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `Channel<DebugEvent>` (unbounded) | `BlockingCollection<DebugEvent>` | Blocking collection is thread-blocking; Channel integrates with async/await and is zero-alloc for fast writes |
| `[GeneratedComClass]` | Manual `ComWrappers` subclass | Manual `ComWrappers` requires much more boilerplate; `[GeneratedComClass]` generates the vtable automatically |
| Dedicated `Thread` for ICorDebug | `Task.Run` | `Task.Run` uses threadpool threads that can be reclaimed; dedicated `Thread` with `IsBackground=true` ensures a stable apartment and predictable lifetime |

---

## Architecture Patterns

### Recommended Class Structure

```
src/DebuggerNetMcp.Core/Engine/
├── DotnetDebugger.cs         # public API + Channel + dedicated thread loop
├── ManagedCallbackHandler.cs # [GeneratedComClass] implementing both callback interfaces
├── Models.cs                 # already exists (Phase 2)
├── PdbReader.cs              # already exists (Phase 2)
└── VariableReader.cs         # already exists (Phase 2)
```

### Pattern 1: LaunchAsync Call Sequence

**What:** The correct order for launching a .NET process and receiving the ICorDebug pointer.

**When to use:** Every call to `LaunchAsync`.

**Sequence:**
```
1. dotnet build -c Debug  (Process.Start + WaitForExitAsync)
2. DbgShimInterop.Load()  (if not already loaded)
3. Create RuntimeStartupCallback delegate
4. DbgShimInterop.KeepAlive(callback)    ← CRITICAL: must come before step 5
5. DbgShimInterop.CreateProcessForLaunch(
       lpCommandLine: "dotnet <app.dll>",
       bSuspendProcess: true,
       lpEnvironment: IntPtr.Zero,
       lpCurrentDirectory: null,
       out processId,
       out resumeHandle)
6. DbgShimInterop.RegisterForRuntimeStartup(
       processId,
       callback,            ← the delegate from step 3
       IntPtr.Zero,
       out unregisterToken)
7. DbgShimInterop.ResumeProcess(resumeHandle)
8. DbgShimInterop.CloseResumeHandle(resumeHandle)
9. --- WAIT --- callback fires on native thread:
       void Callback(IntPtr pCordb, IntPtr parameter, int hr) {
           if (hr != 0) { /* signal failure */ return; }
           var corDebug = (ICorDebug)Marshal.GetObjectForIUnknown(pCordb);
           corDebug.Initialize();
           corDebug.SetManagedHandler(_callbackHandler);
           // pProcess NOT needed here; ICorDebugProcess arrives in CreateProcess callback
           // Write StartedEvent to Channel
       }
```

**Source:** Official docs — `RegisterForRuntimeStartup`, `CreateProcessForLaunch`, `PSTARTUP_CALLBACK`
(https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/registerforruntimestartup-function)

**Critical note on SIGSEGV kernel 6.12+:**
The `RuntimeStartupCallback` fires on a thread created by `libdbgshim.so`. If the delegate has been GC'd before it fires, the crash occurs. `DbgShimInterop.KeepAlive(callback)` stores the delegate in a static field. This MUST be called before `RegisterForRuntimeStartup`.

### Pattern 2: ManagedCallbackHandler with [GeneratedComClass]

**What:** The COM callback class that receives all ICorDebug debug events.

**When to use:** Single instance created once; passed to `ICorDebug.SetManagedHandler`.

```csharp
// Source: official [GeneratedComClass] docs + ICorDebugManagedCallback pattern
// https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshalling.generatedcomclassattribute

[GeneratedComClass]
internal sealed partial class ManagedCallbackHandler
    : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    private readonly ChannelWriter<DebugEvent> _writer;
    private ICorDebugProcess? _process;

    public ManagedCallbackHandler(ChannelWriter<DebugEvent> writer) =>
        _writer = writer;

    public void Breakpoint(ICorDebugAppDomain pAppDomain,
                           ICorDebugThread pThread,
                           ICorDebugBreakpoint pBreakpoint)
    {
        // ... build BreakpointHitEvent ...
        _writer.TryWrite(evt);
        pAppDomain.Continue(0);      // ← MUST call Continue or process hangs
    }

    public void LoadModule(ICorDebugAppDomain pAppDomain, ICorDebugModule pModule)
    {
        // Track module, resolve pending breakpoints
        pAppDomain.Continue(0);      // ← MUST call Continue
    }

    // All 26 methods in ICorDebugManagedCallback:
    //   EVERY method must end with pAppDomain.Continue(0)
    //   or pProcess.Continue(0) if no AppDomain is available.
    //
    // If a method returns E_NOTIMPL ICorDebug MAY auto-continue,
    // but this is not guaranteed. Always call Continue explicitly.

    // ICorDebugManagedCallback2: 8 methods, same rule.
    public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread,
                          ICorDebugFrame? pFrame, uint nOffset,
                          CorDebugExceptionCallbackType dwEventType, uint dwFlags)
    {
        // ...
        pAppDomain.Continue(0);
    }
    // ... remaining 7 methods ...
}
```

**Key invariant (HIGH confidence, verified by Mike Stall's canonical blog):**
Every `ICorDebugManagedCallback` method is called while the debuggee is STOPPED. Returning without calling `Continue()` leaves the debuggee permanently paused. This is the single most common implementation mistake.

Source: https://learn.microsoft.com/en-us/archive/blogs/jmstall/empty-implementation-of-icordebugmanagedcallback

### Pattern 3: Dedicated Thread + Channel Bridge

**What:** The ICorDebug thread loop and event bridge pattern.

```csharp
// Source: System.Threading.Channels official docs
// https://learn.microsoft.com/en-us/dotnet/core/extensions/channels

// In DotnetDebugger constructor:
_eventChannel = Channel.CreateUnbounded<DebugEvent>(
    new UnboundedChannelOptions
    {
        SingleWriter = true,   // only callback thread writes
        SingleReader = false,  // multiple MCP tool calls may read
        AllowSynchronousContinuations = false  // prevent deadlock if reader is on same thread
    });

_callbackHandler = new ManagedCallbackHandler(_eventChannel.Writer);

// The dedicated ICorDebug thread:
_debugThread = new Thread(DebugThreadLoop)
{
    IsBackground = true,
    Name = "ICorDebug-Dispatch"
};
_debugThread.Start();

// Async callers consume events:
public async Task<DebugEvent> WaitForEventAsync(CancellationToken ct)
{
    return await _eventChannel.Reader.ReadAsync(ct);
}
```

**Why `AllowSynchronousContinuations = false`:** If a reader's continuation executes synchronously on the callback thread (which owns the ICorDebug lock), and that continuation tries to call another ICorDebug method, a deadlock can occur. Setting this to `false` forces continuations to run on the thread pool.

### Pattern 4: Pending Breakpoints (SetBreakpointAsync)

**What:** Breakpoints set before the module containing the target method has loaded.

**Sequence:**
```
SetBreakpointAsync(file, line):
  1. PdbReader.FindLocation(dllPath, file, line) → (methodToken, ilOffset)
  2. Look up module in _loadedModules dictionary (keyed by module name)
  3. If found:
       module.GetFunctionFromToken(methodToken, out ICorDebugFunction fn)
       fn.CreateBreakpoint(out ICorDebugFunctionBreakpoint bp)
       bp.Activate(1)
       Store in _breakpoints
  4. If not found (module not loaded yet):
       Store in _pendingBreakpoints list

LoadModule callback:
  5. module.GetName(...) → module name
  6. _loadedModules[name] = module
  7. For each pending breakpoint whose dll matches:
       Resolve as in step 3
       Remove from _pendingBreakpoints
```

**Source:** Verified pattern from lowleveldesign.org Part 4 article and ICorDebugManagedCallback.LoadModule official docs.

### Pattern 5: Stepping (StepOverAsync, StepIntoAsync, StepOutAsync)

**What:** The correct ICorDebugStepper lifecycle.

**Verified sequence (HIGH confidence — official docs + Mike Stall blog):**
```
// StepOver: bStepIn=0, StepInto: bStepIn=1
void DoStep(ICorDebugThread thread, bool stepIn)
{
    thread.CreateStepper(out ICorDebugStepper stepper);
    stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
    stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
    stepper.Step(stepIn ? 1 : 0);  // bStepIn
    // Then call Continue on the controller
    _process.Continue(0);
    // Wait for StepComplete callback
}

// StepOut:
void DoStepOut(ICorDebugThread thread)
{
    thread.CreateStepper(out ICorDebugStepper stepper);
    stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
    stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
    stepper.StepOut();
    _process.Continue(0);
    // Wait for StepComplete callback
}
```

**Note:** Do NOT set `STOP_UNMANAGED` in the unmapped stop mask — it causes StepOut to fail. Use `STOP_NONE` (0) for simple managed-only step operations.

Source: https://learn.microsoft.com/en-us/archive/blogs/jmstall/icordebugstepper-and-using-icordebugstepper2setjmc

### Pattern 6: GetLocalsAsync — Local Variable Enumeration

**What:** Enumerating locals from an active IL frame.

**Sequence:**
```csharp
// Get the active thread's IL frame:
thread.GetActiveFrame(out ICorDebugFrame frame);
var ilFrame = (ICorDebugILFrame)frame;  // COM QI cast

// Get local count via EnumerateLocalVariables:
ilFrame.EnumerateLocalVariables(out ICorDebugValueEnum enumLocals);
// enumLocals.GetCount() — but ICorDebugValueEnum is a stub in Phase 2!
// Alternative: iterate by index with try/catch on CORDBG_E_IL_VAR_NOT_AVAILABLE

// Get local variable names from metadata:
// Requires ICorDebugModule.GetMetaDataInterface → IMetaDataImport
// Then call GetMethodProps(methodToken, ...) to get the local sig token
// Then decode the local variable signature blob for types
// For names: use the PDB (MethodDebugInformation.GetLocalScopes)

// Simpler approach for Phase 3 (names from PDB, values from ICorDebugILFrame):
var locals = new List<VariableInfo>();
for (uint i = 0; ; i++)
{
    try
    {
        ilFrame.GetLocalVariable(i, out ICorDebugValue val);
        string name = GetLocalNameFromPdb(methodToken, i);  // PDB-based
        locals.Add(VariableReader.ReadValue(name, val));
    }
    catch (COMException ex) when (ex.HResult == CORDBG_E_IL_VAR_NOT_AVAILABLE)
    {
        break;  // No more variables at this index
    }
}
```

**Critical note:** `ICorDebugValueEnum` is currently a stub (empty interface body) in Phase 2's ICorDebug.cs. To call `GetCount()` on it, you either need to extend that stub or use the index-iteration approach above. The index-iteration approach is simpler and avoids needing to expand the stub.

**Local variable names:** The PDB `MethodDebugInformation` for a method contains `LocalScope` entries, each with a list of `LocalVariable` entries that have names and slots (indices). This is the correct approach — call `PdbReader`-style metadata reading to get local names by slot index.

### Pattern 7: GetMetaDataInterface for Object Field Names

**What:** Getting field names for `ReadObject` in `VariableReader.cs` (the Phase 2 deferred item).

```csharp
// From ICorDebugModule:
var iid = typeof(IMetaDataImport).GUID;  // IID_IMetaDataImport
module.GetMetaDataInterface(in iid, out IntPtr ppMeta);
var metaImport = (IMetaDataImport)Marshal.GetObjectForIUnknown(ppMeta);

// Enumerate fields of a class:
// objectValue.GetClass(out ICorDebugClass cls)
// cls.GetToken(out uint typedefToken)  ← need to add GetToken to ICorDebugClass stub
// metaImport.EnumFields(ref hEnum, typedefToken, fieldDefs, count, out fetched)
// metaImport.GetFieldProps(fieldToken, ..., fieldName, ...)
// objectValue.GetFieldValue(cls, fieldToken, out ICorDebugValue fieldVal)
```

**Note:** `IMetaDataImport` is a Win32 COM interface that must be declared with `[GeneratedComInterface]` or accessed via `Marshal.GetObjectForIUnknown` and a hand-declared interface. Given the complexity of the full IMetaDataImport interface (~60 methods), the recommended approach for Phase 3 is to declare a minimal `IMetaDataImport` with only the methods needed: `EnumFields`, `GetFieldProps`, `CloseEnum`, and `GetMethodProps`.

### Anti-Patterns to Avoid

- **Calling Continue() twice:** Once per callback invocation, not once per event type. Double-Continue causes corruption.
- **Calling ICorDebug methods from async Task context:** All ICorDebug calls must be on the dedicated thread. Use the command-channel dispatch pattern.
- **Forgetting KeepAlive before RegisterForRuntimeStartup:** The callback delegate gets collected before the native code fires it, causing SIGSEGV or AccessViolationException.
- **Using `Task.Run` for the ICorDebug thread:** Threadpool threads may be reused, and COM apartment state is unpredictable. Use `new Thread(...)`.
- **Setting STOP_UNMANAGED in step unmapped mask:** Causes StepOut to fail with E_INVALIDARG. Always use STOP_NONE for managed-only debugging.
- **Calling ICorDebugFrame as ICorDebugILFrame directly without QI:** The active frame may be a native frame. Wrap the cast in try/catch `InvalidCastException`.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Thread-safe event delivery | Custom lock + List + ManualResetEvent | `Channel<DebugEvent>` | Channel handles synchronization, backpressure, async await, and completion signaling correctly |
| ICorDebug COM callback vtable | Manual vtable + `DllExport` | `[GeneratedComClass]` + `[GeneratedComInterface]` | Source generator produces correct vtable layout for all 26+8 methods automatically |
| IntPtr → ICorDebug conversion | Manual COM QI via `Marshal.GetIUnknownForObject` tricks | `Marshal.GetObjectForIUnknown(pCordb)` | Standard Runtime Callable Wrapper pattern, correct ref-counting |
| GC lifetime of callback delegate | Explicit `GCHandle.Alloc` | `DbgShimInterop.KeepAlive(cb)` | Already built in Phase 2; KeepAlive stores in static field, reliable until debugger disposes |
| PDB-based local variable names | Parse DWARF/PDB manually | `System.Reflection.Metadata` `MethodDebugInformation.GetLocalScopes()` | Already used in PdbReader; `LocalVariable.Name` gives slot-to-name mapping |

---

## Common Pitfalls

### Pitfall 1: Forgetting Continue() in a Callback Method
**What goes wrong:** Debuggee stops permanently. The process is alive but frozen. No further callbacks fire.
**Why it happens:** ICorDebug stops the debuggee before firing a callback. Only `ICorDebugController.Continue()` resumes it.
**How to avoid:** Template every callback method with a `finally { pAppDomain.Continue(0); }` block.
**Warning signs:** Process appears alive (PID exists) but no output, no responses; channel has no new events.

### Pitfall 2: Kernel 6.12+ SIGSEGV on Launch
**What goes wrong:** `dotnet` process crashes with SIGSEGV in `libdbgshim.so` or `libcoreclr.so` immediately after `ResumeProcess`.
**Why it happens:** `RuntimeStartupCallback` fires on a libdbgshim-created thread; if the delegate was GC'd, the callback pointer is dangling.
**How to avoid:** Call `DbgShimInterop.KeepAlive(callback)` BEFORE `RegisterForRuntimeStartup`. The static field keeps the delegate alive.
**Warning signs:** SIGSEGV in stack trace containing `ManagedDebuggerHelpers::Startup`.

### Pitfall 3: Deadlock on Channel with AllowSynchronousContinuations=true
**What goes wrong:** An async caller awaits `ReadAsync()` on the event channel from a context that is later resumed synchronously on the ICorDebug thread. If that continuation tries to call any ICorDebug API, a deadlock occurs.
**Why it happens:** `AllowSynchronousContinuations=true` allows the write to the channel to immediately resume the awaiting consumer on the writer's thread.
**How to avoid:** Always create the event channel with `AllowSynchronousContinuations = false`.
**Warning signs:** Application hangs when step/continue is called after the first breakpoint.

### Pitfall 4: ICorDebugILFrame Cast Failure on Native Frames
**What goes wrong:** `InvalidCastException` when casting `ICorDebugFrame` to `ICorDebugILFrame`.
**Why it happens:** `GetActiveFrame` returns the topmost frame, which may be a native (unmanaged) frame with no IL mapping.
**How to avoid:** Wrap the cast in try/catch. If it fails, traverse the chain to find the first managed frame.
**Warning signs:** Exception in `GetLocalsAsync` or `GetStackTraceAsync` at the very first frame.

### Pitfall 5: Missing ICorDebugClass.GetToken
**What goes wrong:** Cannot enumerate fields on objects because `ICorDebugClass` stub has no methods.
**Why it happens:** Phase 2 left `ICorDebugClass` as an empty stub interface.
**How to avoid:** Add `GetToken(out uint pToken)` to `ICorDebugClass` in `ICorDebug.cs` before implementing `ReadObject` field enumeration.
**Warning signs:** Build error or NullReferenceException when trying to get the TypeDef token from a class.

### Pitfall 6: CORDBG_E_IL_VAR_NOT_AVAILABLE During Prolog
**What goes wrong:** `GetLocalVariable(0, ...)` throws a COMException with HRESULT `0x80131304`.
**Why it happens:** The debugger stopped at the method entry point (prolog), before local variables are initialized.
**How to avoid:** Catch `COMException` with HRESULT `0x80131304` in the local variable enumeration loop.
**Warning signs:** First breakpoint hit at entry point of method; GetLocals throws immediately.

---

## Code Examples

### Minimal ManagedCallbackHandler Declaration

```csharp
// Source: [GeneratedComClass] docs (https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshalling.generatedcomclassattribute)
// and ICorDebugManagedCallback contract (all methods must call Continue)

using System.Runtime.InteropServices.Marshalling;

[GeneratedComClass]
internal sealed partial class ManagedCallbackHandler
    : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    private readonly ChannelWriter<DebugEvent> _events;

    public ManagedCallbackHandler(ChannelWriter<DebugEvent> events) => _events = events;

    public void Breakpoint(ICorDebugAppDomain pAppDomain,
        ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint)
    {
        pThread.GetID(out uint tid);
        _events.TryWrite(new StoppedEvent("breakpoint", (int)tid, null));
        pAppDomain.Continue(0);
    }

    public void StepComplete(ICorDebugAppDomain pAppDomain,
        ICorDebugThread pThread, ICorDebugStepper pStepper, CorDebugStepReason reason)
    {
        pThread.GetID(out uint tid);
        _events.TryWrite(new StoppedEvent("step", (int)tid, null));
        pAppDomain.Continue(0);
    }

    public void ExitProcess(ICorDebugProcess pProcess)
    {
        _events.TryWrite(new ExitedEvent(0));
        _events.Complete();
        // Do NOT call Continue after ExitProcess
    }

    // Minimal pass-through for all other methods:
    public void Break(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread)
        => pAppDomain.Continue(0);
    public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int bUnhandled)
        => pAppDomain.Continue(0);
    // ... (all remaining ICorDebugManagedCallback methods) ...
    // ... (all ICorDebugManagedCallback2 methods) ...
}
```

### RuntimeStartupCallback to Extract ICorDebug

```csharp
// Source: PSTARTUP_CALLBACK docs
// https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/pstartup_callback-function-pointer

void OnRuntimeStarted(IntPtr pCordb, IntPtr parameter, int hr)
{
    if (hr != 0)
    {
        _launchTcs.SetException(new Exception($"Runtime startup failed: HRESULT 0x{hr:X8}"));
        return;
    }

    var corDebug = (ICorDebug)Marshal.GetObjectForIUnknown(pCordb);
    corDebug.Initialize();
    corDebug.SetManagedHandler(_callbackHandler);

    // ICorDebugProcess arrives separately in CreateProcess callback.
    // Just signal that ICorDebug is ready.
    _launchTcs.SetResult(corDebug);
}
```

### Channel Setup (single-writer pattern)

```csharp
// Source: System.Threading.Channels docs
// https://learn.microsoft.com/en-us/dotnet/core/extensions/channels

_eventChannel = Channel.CreateUnbounded<DebugEvent>(new UnboundedChannelOptions
{
    SingleWriter = true,
    SingleReader = false,
    AllowSynchronousContinuations = false
});

// Callback thread writes (fire-and-forget — TryWrite always succeeds for unbounded):
_eventChannel.Writer.TryWrite(new StoppedEvent("breakpoint", threadId, frame));

// Async consumer reads with cancellation:
DebugEvent evt = await _eventChannel.Reader.ReadAsync(cancellationToken);
```

### ICorDebugStepper — StepOver

```csharp
// Source: ICorDebugStepper docs + Mike Stall's stepping blog post
// https://learn.microsoft.com/en-us/archive/blogs/jmstall/icordebugstepper-and-using-icordebugstepper2setjmc

void StepOver(ICorDebugThread thread)
{
    thread.CreateStepper(out ICorDebugStepper stepper);
    stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
    stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
    stepper.Step(0);            // 0 = step-over, 1 = step-into
    _process!.Continue(0);     // Must continue after setting up the step
    // StepComplete callback fires when step is done
}

void StepOut(ICorDebugThread thread)
{
    thread.CreateStepper(out ICorDebugStepper stepper);
    stepper.SetInterceptMask(CorDebugIntercept.INTERCEPT_NONE);
    stepper.SetUnmappedStopMask(CorDebugUnmappedStop.STOP_NONE);
    stepper.StepOut();
    _process!.Continue(0);
}
```

### SetBreakpointAsync — Core Resolution

```csharp
void ResolveBreakpointOnModule(ICorDebugModule module,
    int breakpointId, int methodToken, int ilOffset)
{
    module.GetFunctionFromToken((uint)methodToken, out ICorDebugFunction fn);
    fn.CreateBreakpoint(out ICorDebugFunctionBreakpoint bp);
    bp.Activate(1);  // 1 = active
    _activeBreakpoints[breakpointId] = bp;
}
```

### GetLocalsAsync — Index-based Iteration

```csharp
List<VariableInfo> GetLocals(ICorDebugILFrame ilFrame, int methodToken, string dllPath)
{
    const int CORDBG_E_IL_VAR_NOT_AVAILABLE = unchecked((int)0x80131304);
    var result = new List<VariableInfo>();

    for (uint i = 0; i < 256; i++)  // 256 is a safe upper bound
    {
        try
        {
            ilFrame.GetLocalVariable(i, out ICorDebugValue val);
            string name = GetLocalNameFromPdb(dllPath, methodToken, i);
            result.Add(VariableReader.ReadValue(name ?? $"local_{i}", val));
        }
        catch (COMException ex) when (ex.HResult == CORDBG_E_IL_VAR_NOT_AVAILABLE)
        {
            break;
        }
        catch (Exception)
        {
            break;
        }
    }
    return result;
}
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Manual `ComWrappers` subclass for COM callbacks | `[GeneratedComClass]` source generator | .NET 8 (2023) | Eliminates ~200 lines of vtable boilerplate; source-gen validates at compile time |
| `BlockingCollection<T>` for thread communication | `System.Threading.Channels` | .NET Core 3.0 (2019) | Async-compatible, ValueTask-based, zero-alloc fast path |
| `ICorDebug.CreateProcess` (Windows-only) | `CreateProcessForLaunch` + `RegisterForRuntimeStartup` | .NET Core 2.1 (2018) | Works cross-platform (Linux, macOS, Windows) |
| `CLRCreateInstance` to get ICorDebug | `RegisterForRuntimeStartup` callback | .NET Core 3.0+ | No CLSIDs needed; runtime delivers ICorDebug directly |

**Deprecated/outdated:**
- `CLRCreateInstance` + direct `ICorDebug.Initialize`: Windows-only; replaced by `RegisterForRuntimeStartup` for cross-platform launch.
- `DebugActiveProcess`: Windows-only for attach. Use `RegisterForRuntimeStartup` for cross-platform attach too (it handles the case where runtime is already loaded).

---

## Open Questions

1. **ICorDebugValueEnum stub expansion**
   - What we know: `ICorDebugValueEnum` is an empty stub in Phase 2's `ICorDebug.cs`. It needs `GetCount(out uint pcValues)` for local variable count without iteration.
   - What's unclear: Whether to add `GetCount` to the stub or just use index-based iteration (which is safer).
   - Recommendation: Use index-based iteration with `CORDBG_E_IL_VAR_NOT_AVAILABLE` sentinel; no stub expansion needed.

2. **ICorDebugClass.GetToken for field enumeration**
   - What we know: Phase 2 left `ICorDebugClass` as an empty stub. `ReadObject` in `VariableReader.cs` returns `<object>` placeholder.
   - What's unclear: Whether Phase 3 should complete field enumeration via `IMetaDataImport` or defer to Phase 4.
   - Recommendation: Add `GetToken(out uint pToken)` to the `ICorDebugClass` stub; declare a minimal `IMetaDataImport` interface; implement field name resolution in `VariableReader.ReadObject`. This is required for `GetLocalsAsync` to show object field values.

3. **Minimal IMetaDataImport interface declaration**
   - What we know: `ICorDebugModule.GetMetaDataInterface` returns an `IntPtr` that must be `Marshal.GetObjectForIUnknown`'d. The target interface needs `[GeneratedComInterface]` + correct GUID.
   - What's unclear: Whether `[GeneratedComInterface]` works for `IMetaDataImport` (which has `~60 methods`) or whether declaring only the needed methods causes vtable issues.
   - Recommendation: Declare a minimal `IMetaDataImportMinimal` with only `EnumFields`, `GetFieldProps`, `CloseEnum`. Use a separate GUID from the real `IMetaDataImport` (`7DAC8207-D3AE-4C75-9B67-92801A497D44`) for QI. This is the same GUID — declaring fewer methods is safe as long as the order matches the real vtable.

4. **AttachAsync sequence (not in scope for core deliverable)**
   - What we know: `RegisterForRuntimeStartup` works for attach scenarios too (even before the runtime loads).
   - What's unclear: Whether `DebugActiveProcess` is needed at all for attach, or whether `RegisterForRuntimeStartup` with an existing PID is sufficient.
   - Recommendation: For Phase 3, implement `AttachAsync` identically to `LaunchAsync` but skip the `CreateProcessForLaunch` step — call `RegisterForRuntimeStartup(existingPid, ...)` directly. The process runs unsuspended; the callback fires when ICorDebug is ready.

---

## Sources

### Primary (HIGH confidence)
- `RegisterForRuntimeStartup` — https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/registerforruntimestartup-function
- `PSTARTUP_CALLBACK` — https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/pstartup_callback-function-pointer
- `CreateProcessForLaunch` — https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/createprocessforlaunch-function
- `GeneratedComClassAttribute` — https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshalling.generatedcomclassattribute
- `System.Threading.Channels` — https://learn.microsoft.com/en-us/dotnet/core/extensions/channels
- `ICorDebugStepper` stepping sequence — https://learn.microsoft.com/en-us/archive/blogs/jmstall/icordebugstepper-and-using-icordebugstepper2setjmc
- `ICorDebugManagedCallback` Continue requirement — https://learn.microsoft.com/en-us/archive/blogs/jmstall/empty-implementation-of-icordebugmanagedcallback

### Secondary (MEDIUM confidence)
- Pending breakpoint + LoadModule pattern — lowleveldesign.org Part 4 (verified against official LoadModule docs)
- Object field enumeration pattern — csharpcodi.com ICorDebug examples (cross-verified with official ICorDebugObjectValue docs)
- STA / apartment state for dedicated thread — multiple MSDN/StackOverflow sources (consistent)

### Tertiary (LOW confidence, needs validation)
- `IMetaDataImport` partial vtable safety (declaring fewer methods) — deduced from COM vtable contract; not explicitly confirmed for `[GeneratedComInterface]`

---

## Metadata

**Confidence breakdown:**
- Launch sequence (CreateProcessForLaunch + RegisterForRuntimeStartup + KeepAlive): HIGH — all three APIs documented in official .NET docs
- `[GeneratedComClass]` callback pattern: HIGH — documented in official .NET 8+ interop docs
- `ICorDebugManagedCallback` Continue requirement: HIGH — canonical blog post from ICorDebug team
- Stepping (ICorDebugStepper): HIGH — official docs + team blog post
- Channel pattern: HIGH — official .NET docs
- Breakpoint resolution (GetFunctionFromToken + CreateBreakpoint): HIGH — corroborated by multiple sources
- Local variable enumeration (index-based iteration): HIGH — official ICorDebugILFrame docs confirm pattern
- IMetaDataImport field enumeration: MEDIUM — partial vtable safety is an educated deduction

**Research date:** 2026-02-22
**Valid until:** 2026-04-22 (ICorDebug API stable for years; Channel API stable since .NET Core 3.0)
