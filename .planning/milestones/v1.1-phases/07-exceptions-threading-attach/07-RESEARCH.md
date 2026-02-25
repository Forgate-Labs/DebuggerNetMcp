# Phase 7: Exceptions, Threading & Attach - Research

**Researched:** 2026-02-23
**Domain:** ICorDebug exception callbacks, multi-thread inspection, process attach
**Confidence:** HIGH

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| EXCP-01 | Debugger notifies unhandled exception (second-chance) via ExceptionEvent with type and message — process does not terminate silently | ICorDebugManagedCallback2.Exception with DEBUG_EXCEPTION_UNHANDLED + ICorDebugThread.GetCurrentException() to read exception object |
| EXCP-02 | Debugger supports first-chance exception notifications (optional — configurable via debug_launch) | Same Exception callback with DEBUG_EXCEPTION_FIRST_CHANCE; flag on ManagedCallbackHandler controls whether to stop or continue |
| THRD-01 | debug_variables returns variables for the correct thread when multiple threads exist | GetLocalsAsync needs optional threadId parameter; uses _process.GetThread(threadId) instead of CurrentStoppedThreadId |
| THRD-02 | debug_stacktrace returns stack of all active threads (or specified thread by ID) | GetStackTraceAsync needs optional threadId param; enumerate all threads via ICorDebugController.EnumerateThreads when no ID given |
| THRD-03 | debug_pause stops all threads in the process, not just the main thread | ICorDebugProcess.Stop(0) already stops all threads — needs verification; SetAllThreadsDebugState may be needed |
| ATCH-01 | debug_attach(pid) connects to running .NET process and returns state="attached" with process info | AttachAsync already calls RegisterForRuntimeStartup but does not wait for a stopping event; need to wait for CreateProcess callback and return structured info |
</phase_requirements>

---

## Summary

Phase 7 addresses three distinct areas that are partially implemented but not fully functional: exception reporting with rich type/message info, multi-thread inspection, and the attach-to-running-process workflow.

**Exception handling (EXCP-01, EXCP-02):** The v1 `Exception` callback in `ManagedCallbackHandler` already stops on unhandled exceptions and fires an `ExceptionEvent`, but reports `"<unhandled>"` as the type and `"Unhandled exception"` as the message — placeholder text only. The fix is to call `pThread.GetCurrentException()` in the callback, dereference the `ICorDebugReferenceValue`, cast to `ICorDebugObjectValue`, and read the `_message` field (already implemented in `VariableReader.ReadObject`). The v2 `ICorDebugManagedCallback2.Exception` also fires for `DEBUG_EXCEPTION_UNHANDLED` and already has a duplicate write — the v1 callback must be silenced when the v2 callback handles it to avoid double-reporting. For EXCP-02, a `NotifyFirstChanceExceptions` bool flag on `DotnetDebugger`/`LaunchAsync` controls whether first-chance events produce a stopping `ExceptionEvent` or continue silently.

**Multi-threading (THRD-01, THRD-02, THRD-03):** `GetLocalsAsync` and `GetStackTraceAsync` currently use `GetCurrentThread()` which reads `_callbackHandler.CurrentStoppedThreadId`. To support THRD-01 and THRD-02, both methods need an optional `threadId` parameter. When `threadId == 0` (default), behavior is unchanged; when non-zero, `_process.GetThread(threadId)` is used directly. For THRD-02 all-threads mode, a new `GetAllThreadsAsync()` helper enumerates threads via `ICorDebugController.EnumerateThreads` — this requires a workaround because `ICorDebugThreadEnum.Next()` has LPArray marshaling issues (see existing `GetCurrentThread()` comment). The workaround used in `GetCurrentThread()` is `_process.GetThread(tid)` with known IDs; for enumeration a small loop pattern fetching one at a time is safe. THRD-03: `_process.Stop(0)` (used in `PauseAsync`) stops the entire process at the ICorDebug level — all threads are suspended. This is already correct behavior; the success criterion is that after `debug_pause`, `debug_stacktrace` can return frames from multiple threads.

**Process attach (ATCH-01):** `AttachAsync` calls `RegisterForRuntimeStartup(processId, ...)` and returns immediately with `state="running"`. This is incomplete: the attach does not wait for the `CreateProcess` callback to confirm the connection succeeded, and does not return process information (name, pid). The fix mirrors `LaunchAsync`: set a flag to stop at `CreateProcess` (or a new `StopAtAttach` flag), wait for the stopping event, extract process info, and return `state="stopped"`. Alternatively, return `state="attached"` without stopping (matching the requirement text). The distinction matters: the requirement says `state="attached"`, not `state="stopped"`, suggesting the process should continue running after attach.

**Primary recommendation:** Implement in three focused plans: (1) Exception type/message extraction, (2) Multi-thread inspection with optional threadId, (3) Attach completion + HelloDebug threading section.

---

## Standard Stack

### Core (all already in use)
| Component | Purpose | Notes |
|-----------|---------|-------|
| `ICorDebugManagedCallback.Exception` | v1 exception callback — `bUnhandled != 0` for second-chance | Already connected; needs exception object read |
| `ICorDebugManagedCallback2.Exception` | v2 exception callback — `CorDebugExceptionCallbackType` enum | Already connected; `DEBUG_EXCEPTION_UNHANDLED = 4` |
| `ICorDebugThread.GetCurrentException` | Returns `ICorDebugValue` of the thrown exception | Must be called while stopped in exception callback |
| `ICorDebugController.EnumerateThreads` | Returns `ICorDebugThreadEnum` of all threads | LPArray marshaling workaround needed (see patterns) |
| `ICorDebugController.SetAllThreadsDebugState` | Suspend/resume all threads | Available on `ICorDebugProcess` via inheritance |
| `ICorDebug.DebugActiveProcess` | Attach to running process | Already used in `OnRuntimeStarted` for the launched process |
| `RegisterForRuntimeStartup` | Hooks into .NET runtime startup for attach | Already used in `AttachToProcess` |

---

## Architecture Patterns

### Pattern 1: Reading Exception Type and Message in Callback

The v1 `Exception` callback fires on the ICorDebug thread. The thread is stopped. Call `GetCurrentException()`, dereference, read the exception object.

```csharp
// In ManagedCallbackHandler.Exception (v1 callback, bUnhandled != 0)
// Source: existing VariableReader.ReadObject pattern + ICorDebugThread.GetCurrentException
public void Exception(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, int bUnhandled)
{
    if (bUnhandled != 0)
    {
        pThread.GetID(out uint tid);
        var (exType, exMessage) = TryReadExceptionInfo(pThread);
        _events.TryWrite(new ExceptionEvent(exType, exMessage, (int)tid, true));
        // DO NOT call Continue — this is a stopping event
    }
    else if (_notifyFirstChance)
    {
        pThread.GetID(out uint tid);
        var (exType, exMessage) = TryReadExceptionInfo(pThread);
        _events.TryWrite(new ExceptionEvent(exType, exMessage, (int)tid, false));
        // DO NOT call Continue — first-chance stop requested
    }
    else
    {
        pAppDomain.Continue(0);
    }
}

private (string type, string message) TryReadExceptionInfo(ICorDebugThread pThread)
{
    try
    {
        pThread.GetCurrentException(out ICorDebugValue exVal);
        if (exVal == null) return ("<unknown>", "No exception object");

        // Dereference reference value
        if (exVal is ICorDebugReferenceValue refVal)
        {
            refVal.IsNull(out int isNull);
            if (isNull != 0) return ("<unknown>", "Exception reference is null");
            refVal.Dereference(out ICorDebugValue innerVal);
            exVal = innerVal;
        }

        if (exVal is ICorDebugObjectValue objVal)
        {
            objVal.GetClass(out ICorDebugClass cls);
            cls.GetModule(out ICorDebugModule module);
            cls.GetToken(out uint typedefToken);

            // Get type name from PE metadata
            string dllPath = GetModulePath(module);
            string typeName = VariableReader.GetTypeName(objVal)
                           ?? PdbReader.GetTypeNameFromToken(dllPath, typedefToken)
                           ?? "<unknown>";

            // Read _message field — Exception stores message in _message
            string message = TryReadExceptionMessage(objVal, cls, dllPath, typedefToken);
            return (typeName, message);
        }
        return ("<unknown>", "Could not read exception object");
    }
    catch (Exception ex)
    {
        return ("<unknown>", $"Failed to read exception: {ex.Message}");
    }
}
```

**Key insight:** `VariableReader.ReadObject` already knows how to read `_message` and `_HResult` from exception objects (verified in Phase 6 testing with `DivideByZeroException`). The same `ReadInstanceFieldsFromPE` + inheritance loop handles Exception hierarchy. The callback just needs to call into that infrastructure.

**Double-reporting guard:** The v1 `Exception` and v2 `Exception` callbacks both fire. Currently both write an `ExceptionEvent` for unhandled exceptions. Fix: v1 callback should only handle the case when v2 is not available, or v1 delegates to v2. Simplest fix: the v2 `ICorDebugManagedCallback2.Exception` with `DEBUG_EXCEPTION_UNHANDLED` becomes the canonical handler; the v1 `Exception` continues silently for `bUnhandled != 0` (since v2 will also fire). Verify this ordering experimentally — on .NET 10, v2 fires after v1.

**Simpler approach:** Keep v1 as the canonical unhandled handler (it fires first), silence v2's unhandled case by adding a `_v1HandledUnhandled` flag. Or: make v1 only run if v2 will not fire (check if `ICorDebugManagedCallback2` is registered — it always is). Actually the cleanest: let v1 handle `bUnhandled != 0` as a stopping event (current behavior), and let v2's `DEBUG_EXCEPTION_UNHANDLED` continue silently (since v1 already stopped). This is already partially true — v1 stops, v2 would then fire but since the process is already stopped, v2 fires after... wait, v2 fires as a *separate callback* only if the runtime decides to send it. Research shows v2 `Exception` replaces v1 for the same event — if v2 is registered, v1 fires first then v2 fires. On modern .NET, both fire for the same exception. Solution: add a guard in v2 that skips `DEBUG_EXCEPTION_UNHANDLED` if v1 already wrote the stopping event. Use a `bool _exceptionStopPending` flag on ManagedCallbackHandler.

### Pattern 2: Thread Enumeration via EnumerateThreads

`ICorDebugThreadEnum.Next()` requires `[MarshalAs(UnmanagedType.LPArray, SizeParamIndex=0)]` which has marshaling issues. The existing code comment in `GetCurrentThread()` confirms this. Pattern: enumerate one at a time with a loop, reading thread IDs into a list first.

```csharp
// Source: existing GetCurrentThread() comment + ICorDebugController.EnumerateThreads interface
private List<uint> GetAllThreadIds()
{
    var ids = new List<uint>();
    _process!.EnumerateThreads(out ICorDebugThreadEnum threadEnum);
    var arr = new ICorDebugThread[1];
    while (true)
    {
        threadEnum.Next(1, arr, out uint fetched);
        if (fetched == 0) break;
        arr[0].GetID(out uint tid);
        ids.Add(tid);
    }
    return ids;
}
```

**Note:** `EnumerateThreads` is defined on `ICorDebugController` which `ICorDebugProcess` inherits. The interface already has `EnumerateThreads(out ICorDebugThreadEnum ppThreads)` defined (line 134 of ICorDebug.cs). `ICorDebugThreadEnum.Next` is at line 509 with the LPArray attribute — this was flagged as problematic but `celt=1` with a single-element array works around the issue since there's no actual LPArray for count=1.

### Pattern 3: GetStackTraceAsync/GetLocalsAsync with optional threadId

Both methods follow the same dispatch pattern. Adding an optional `threadId` parameter is straightforward:

```csharp
// Modified signature — default 0 means "use CurrentStoppedThreadId"
public async Task<IReadOnlyList<StackFrameInfo>> GetStackTraceAsync(
    uint threadId = 0, CancellationToken ct = default)

// Inside the dispatch lambda:
ICorDebugThread thread = threadId != 0
    ? GetThreadById(threadId)
    : GetCurrentThread();

private ICorDebugThread GetThreadById(uint threadId)
{
    _process!.GetThread(threadId, out ICorDebugThread thread);
    if (thread is null)
        throw new InvalidOperationException($"Thread {threadId} not found");
    return thread;
}
```

**All-threads stacktrace for THRD-02:** Return a dictionary/list of per-thread stack frames:

```csharp
public async Task<IReadOnlyDictionary<uint, IReadOnlyList<StackFrameInfo>>>
    GetAllThreadStackTracesAsync(CancellationToken ct = default)
```

Alternatively, the MCP tool `debug_stacktrace` returns a JSON with a `threads` array when no `thread_id` is specified, or a single thread's frames when specified.

### Pattern 4: debug_launch with firstChanceExceptions option

```csharp
// LaunchAsync signature extended
public async Task LaunchAsync(string projectPath, string appDllPath,
    bool notifyFirstChanceExceptions = false,
    CancellationToken ct = default)

// Set on handler
_callbackHandler.NotifyFirstChanceExceptions = notifyFirstChanceExceptions;
```

MCP tool `debug_launch` adds:
```csharp
[Description("If true, stop on every thrown exception (first-chance). Default false.")]
bool firstChanceExceptions = false
```

### Pattern 5: Attach completion and return state="attached"

Current `AttachAsync` dispatches `AttachToProcess` (calls `RegisterForRuntimeStartup`) then returns immediately with `state="running"`. The requirement says `state="attached"` with process information.

The issue is that `RegisterForRuntimeStartup` for an already-running process fires the callback asynchronously when the runtime is found. The `CreateProcess` callback will fire on the ICorDebug thread. The MCP tool currently doesn't wait for this.

Fix option A (wait for CreateProcess, then continue — returns `state="attached"` with process running):
```csharp
// In AttachAsync: after dispatch, wait for CreateProcess event
// Set a flag: StopAtCreateProcess = false (don't stop, just wait for the event to confirm attach)
// But we need to know when attach is confirmed

// Alternative: use a TaskCompletionSource set in OnProcessCreated callback
_callbackHandler.OnProcessCreated = proc =>
{
    proc.GetID(out uint pid);
    attachTcs.SetResult(pid);
};
var pid = await attachTcs.Task.WaitAsync(ct);
return new { state = "attached", pid };
```

Fix option B (stop the process on attach, inspect, then release):
This would require `StopAtCreateProcess = true` for attach too, but the requirement says `state="attached"` not `state="stopped"` — keep process running.

**Chosen approach:** Add `OnProcessAttached` callback in `ManagedCallbackHandler` (similar to `OnProcessCreated`), set from `AttachAsync` before dispatch, complete when `CreateProcess` fires. Return `state="attached"` with pid and process info.

### Pattern 6: Getting process name for attach info

```csharp
// After attach is confirmed (CreateProcess callback fired):
// Get process name from the process ID using System.Diagnostics.Process
var proc = System.Diagnostics.Process.GetProcessById((int)pid);
string processName = proc.ProcessName;  // e.g., "dotnet" or "HelloDebug"
```

This runs on the caller's thread (not the debug thread) — safe.

### Anti-Patterns to Avoid

- **Double-reporting exceptions:** v1 and v2 Exception callbacks both fire for the same unhandled exception. Using a `_exceptionStopPending` flag or having one of the two continue silently prevents the MCP caller from receiving two events.
- **Calling Continue after stopping event:** Any callback that writes a stopping event (unhandled exception, first-chance if enabled) must NOT call `Continue`. If reading exception info throws, fall back to continuing to avoid deadlock.
- **Thread enumeration with large celt:** `ICorDebugThreadEnum.Next(celt > 1, ...)` has LPArray marshaling issues. Always use `celt=1` in a loop.
- **Accessing _process before attach confirmed:** `_process` is set in `OnProcessCreated` callback. Between `AttachAsync` dispatch and the callback, `_process` is null. The `OnProcessAttached` TCS ensures callers wait.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead |
|---------|-------------|-------------|
| Exception type name | Custom PE metadata reader for exception type | `VariableReader.GetTypeName(objVal)` already reads type name from ICorDebugObjectValue |
| Exception message | Manual field walk | `VariableReader.ReadObject` (used in Phase 6) already reads `_message` field via inheritance loop |
| Thread ID list | Platform-specific /proc parsing | `ICorDebugController.EnumerateThreads` + ICorDebugThread.GetID |
| Process name for attach | /proc/PID/comm parsing | `System.Diagnostics.Process.GetProcessById(pid).ProcessName` |

---

## Common Pitfalls

### Pitfall 1: Double Exception Events (v1 + v2 Callbacks)
**What goes wrong:** Both `ICorDebugManagedCallback.Exception` (v1) and `ICorDebugManagedCallback2.Exception` (v2) fire for the same unhandled exception. Two `ExceptionEvent` entries are written to the channel. The MCP caller sees two `type="exception"` events.
**Why it happens:** ICorDebug calls v1 then v2 for the same exception event when both callbacks are registered.
**How to avoid:** Use a `bool _exceptionStopPending` flag. v1 sets it when writing a stopping event; v2 clears it and continues silently (since v1 already handled it). Or: make v2's `DEBUG_EXCEPTION_UNHANDLED` case always continue silently since v1 already stopped.
**Warning signs:** Receiving two exception events in succession in the channel; `debug_continue` after exception causes process to exit immediately (the second stop wasn't real).

### Pitfall 2: GetCurrentException Returns Null After Continue
**What goes wrong:** `pThread.GetCurrentException()` must be called *during* the exception callback, while the thread is stopped. After `Continue()` is called, the exception context is gone.
**Why it happens:** Exception object lifetime is tied to the exception's active unwinding state.
**How to avoid:** Read the exception info synchronously inside the callback, before any `Continue()` call. The `TryReadExceptionInfo` helper must be called before writing the event or calling Continue.

### Pitfall 3: ICorDebugThreadEnum.Next Marshaling with celt > 1
**What goes wrong:** Calling `Next(N, array, out fetched)` with N > 1 can fail due to LPArray marshaling for COM interface arrays in source-generated COM interop.
**Why it happens:** `[MarshalAs(UnmanagedType.LPArray, SizeParamIndex=0)]` for `ICorDebugThread[]` in source-generated COM interop has known issues on Linux.
**How to avoid:** Always use `celt=1` with a single-element array in a while loop. This pattern is confirmed working for `ICorDebugChainEnum.Next` and `ICorDebugFrameEnum.Next` in `GetStackTraceAsync`.
**Warning signs:** HRESULT errors or garbage data when calling Next with celt > 1.

### Pitfall 4: Attach Returns Before CreateProcess Fires
**What goes wrong:** `AttachAsync` returns `state="running"` but the attach hasn't been confirmed yet — `_process` is still null. If the caller immediately calls `debug_variables`, it throws `NullReferenceException`.
**Why it happens:** `RegisterForRuntimeStartup` is asynchronous — the callback fires when the runtime is found in the target process.
**How to avoid:** Use a `TaskCompletionSource` set in `OnProcessCreated`. `AttachAsync` awaits this TCS before returning. Return `state="attached"` only after `_process` is set.
**Warning signs:** `debug_variables` after `debug_attach` throws "No current stopped thread" or NullReferenceException.

### Pitfall 5: First-Chance Exceptions Flood
**What goes wrong:** Enabling first-chance notifications (`EXCP-02`) on an app that uses exceptions for control flow (e.g., JSON parsing, IO, LINQ) generates hundreds of ExceptionEvents, filling the channel and overwhelming the MCP caller.
**Why it happens:** Many .NET operations throw and catch exceptions internally.
**How to avoid:** Make `notifyFirstChanceExceptions` default to `false`. Optionally add a filter parameter (exception type name prefix). Document clearly in the MCP tool description.
**Warning signs:** Channel fills up immediately after `debug_continue`; tool becomes unresponsive.

### Pitfall 6: ICorDebugProcess.Stop Semantics
**What goes wrong:** Calling `_process.Stop(0)` in `PauseAsync` stops the process, but the `Break` callback fires on the ICorDebug thread to signal the stop. If `PauseAsync` doesn't wait for the `Break` event, the caller may not know which thread stopped.
**Why it happens:** `Stop()` is asynchronous — it requests the stop but the process may not be stopped when the method returns.
**How to avoid:** `PauseAsync` already dispatches via the command channel, which ensures ordering. The `Break` callback updates `CurrentStoppedThreadId`. For THRD-03 verification, after `debug_pause`, call `debug_stacktrace` without arguments (all threads) to confirm all threads are accessible.
**Current state:** `PauseAsync` calls `_process.Stop(0)` — this suspends all managed threads. No per-thread stop needed.

---

## Code Examples

### Reading Exception Object in Callback

```csharp
// Source: existing VariableReader.GetTypeName + ICorDebugThread.GetCurrentException
private static (string typeName, string message) TryReadExceptionInfo(ICorDebugThread pThread)
{
    try
    {
        pThread.GetCurrentException(out ICorDebugValue exVal);
        if (exVal == null) return ("<unknown>", "No exception available");

        // Dereference if needed
        ICorDebugValue actual = exVal;
        exVal.GetType(out uint typeRaw);
        if ((CorElementType)typeRaw is CorElementType.Class or CorElementType.Object)
        {
            if (exVal is ICorDebugReferenceValue rv)
            {
                rv.IsNull(out int isNull);
                if (isNull == 0) { rv.Dereference(out actual); }
            }
        }

        string typeName = VariableReader.GetTypeName(actual) ?? "<unknown>";
        string message = "Unhandled exception";

        if (actual is ICorDebugObjectValue objVal)
        {
            objVal.GetClass(out ICorDebugClass cls);
            cls.GetModule(out ICorDebugModule module);
            cls.GetToken(out uint typedefToken);
            string dllPath = GetModulePath(module);

            // Read _message field (inherited from System.Exception)
            message = TryReadStringField(objVal, cls, dllPath, typedefToken, "_message")
                   ?? message;
        }

        return (typeName, message);
    }
    catch { return ("<unknown>", "Exception info unavailable"); }
}
```

### Thread Enumeration (safe pattern)

```csharp
// Source: existing GetCurrentThread() pattern + ICorDebugController.EnumerateThreads
// Must be called on the debug thread (inside DispatchAsync)
private List<ICorDebugThread> GetAllThreads()
{
    var threads = new List<ICorDebugThread>();
    _process!.EnumerateThreads(out ICorDebugThreadEnum threadEnum);
    var arr = new ICorDebugThread[1];
    while (true)
    {
        threadEnum.Next(1, arr, out uint fetched);
        if (fetched == 0) break;
        threads.Add(arr[0]);
    }
    return threads;
}
```

### All-Threads Stack Trace

```csharp
// Returns list of (threadId, frames) pairs
// Source: GetStackTraceAsync chain-walking pattern applied to each thread
public async Task<IReadOnlyList<(uint ThreadId, IReadOnlyList<StackFrameInfo> Frames)>>
    GetAllThreadStackTracesAsync(CancellationToken ct = default)
{
    var tcs = new TaskCompletionSource<...>(RunContinuationsAsynchronously);
    await DispatchAsync(() =>
    {
        var result = new List<(uint, IReadOnlyList<StackFrameInfo>)>();
        var threads = GetAllThreads();
        foreach (var thread in threads)
        {
            thread.GetID(out uint tid);
            var frames = GetStackFramesForThread(thread);
            result.Add((tid, frames));
        }
        tcs.SetResult(result);
    }, ct);
    return await tcs.Task.WaitAsync(ct);
}
```

### Attach with Confirmation

```csharp
// In DotnetDebugger.AttachAsync — wait for CreateProcess callback
public async Task<(uint Pid, string ProcessName)> AttachAsync(
    uint processId, CancellationToken ct = default)
{
    var attachTcs = new TaskCompletionSource<uint>(RunContinuationsAsynchronously);
    _callbackHandler.OnProcessCreated = proc =>
    {
        proc.GetID(out uint pid);
        attachTcs.TrySetResult(pid);
    };

    await DispatchAsync(() => AttachToProcess(processId), ct);

    uint confirmedPid = await attachTcs.Task.WaitAsync(ct);
    string name = System.Diagnostics.Process.GetProcessById((int)confirmedPid).ProcessName;
    return (confirmedPid, name);
}
```

---

## Current State Analysis

### What Already Works

| Feature | Status | Notes |
|---------|--------|-------|
| Unhandled exception stopping | Works | v1 callback stops on `bUnhandled != 0`; v2 also fires |
| ExceptionEvent model | Complete | Has ExceptionType, Message, ThreadId, IsUnhandled |
| ICorDebugProcess.Stop | Works | `PauseAsync` calls `_process.Stop(0)` — stops all threads |
| GetThread(threadId) | Works | `_process.GetThread(tid)` already used in `GetCurrentThread()` |
| DebugActiveProcess | Works | Used in `OnRuntimeStarted` for launched process |
| RegisterForRuntimeStartup | Works | Used in `AttachToProcess` for attach |
| EnumerateThreads interface | Defined | `ICorDebugController.EnumerateThreads` in ICorDebug.cs |

### What Needs Fixing

| Feature | Gap | Fix |
|---------|-----|-----|
| Exception type name | Hardcoded `"<unhandled>"` | Call `TryReadExceptionInfo` in callback |
| Exception message | Hardcoded `"Unhandled exception"` | Read `_message` field from exception object |
| Double exception event | v1 + v2 both write stopping events | Guard in v2 to continue silently |
| First-chance notifications | Always silently continued | `NotifyFirstChanceExceptions` flag |
| GetLocalsAsync thread | Always uses `CurrentStoppedThreadId` | Optional `threadId` parameter |
| GetStackTraceAsync thread | Always uses `CurrentStoppedThreadId` | Optional `threadId` + all-threads mode |
| AttachAsync completion | Returns immediately, no confirmation | Wait for `OnProcessCreated` callback |
| debug_attach return | `state="running"`, minimal info | `state="attached"` with pid + process name |

---

## Plan Decomposition Recommendation

**3 plans, sequenced:**

### Plan 07-01: Exception Info Extraction
- Fix `TryReadExceptionInfo` in `ManagedCallbackHandler`
- Add `NotifyFirstChanceExceptions` flag to handler + `DotnetDebugger.LaunchAsync`
- Silence double-reporting (v1/v2 guard)
- Update MCP `debug_launch` tool description (no signature change needed for EXCP-01; add optional bool for EXCP-02)
- Requirements: EXCP-01, EXCP-02

### Plan 07-02: Multi-Thread Inspection
- Add `GetAllThreadIds()` helper in `DotnetDebugger`
- Add optional `threadId` to `GetLocalsAsync` and `GetStackTraceAsync`
- Add `GetAllThreadStackTracesAsync()` method
- Update MCP `debug_variables` and `debug_stacktrace` tools with optional `thread_id`
- Requirements: THRD-01, THRD-02, THRD-03

### Plan 07-03: Attach Completion + HelloDebug Threading Section + Live Verification
- Fix `AttachAsync` to wait for `OnProcessCreated` callback
- Return `state="attached"` with pid and process name from `debug_attach`
- Add HelloDebug section 20: multi-thread test (two threads, different stacks)
- Add HelloDebug section 21: unhandled exception test
- Live verification of all 6 requirements
- Requirements: ATCH-01, TEST-09 (partial)

---

## Open Questions

1. **v1 vs v2 Exception callback ordering on .NET 10**
   - What we know: Both fire; v1 fires first based on ICorDebug design
   - What's unclear: Does .NET 10 send v2 `DEBUG_EXCEPTION_UNHANDLED` when v1 already handled it? Does it send both?
   - Recommendation: Add logging to both callbacks during implementation and verify experimentally. The `_exceptionStopPending` guard will handle either ordering.

2. **EnumerateThreads with stopped process**
   - What we know: `EnumerateThreads` works on a stopped process; the thread enum is stable when stopped
   - What's unclear: Whether all threads are enumerable after `Stop(0)` (e.g., CLR internal threads)
   - Recommendation: Filter out threads with `CorDebugUserState.USER_BACKGROUND` flag if needed; start without filtering and adjust.

3. **GetCurrentException during first-chance**
   - What we know: `GetCurrentException` is documented to return the current exception
   - What's unclear: Whether it works for first-chance (`bUnhandled == 0`) or only second-chance
   - Recommendation: Test both in implementation; fall back to `"<exception>"` if call fails.

4. **Attach to process that has no PDB**
   - What we know: Attach via `RegisterForRuntimeStartup` works for any .NET process
   - What's unclear: Whether `GetStackTraceAsync` fails or just returns hex tokens for processes without PDB
   - Recommendation: Stack trace already handles missing PDB (returns hex tokens with `TODO` comment); attach should work.

---

## Sources

### Primary (HIGH confidence — code analysis)
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/ManagedCallbackHandler.cs` — exception callbacks, CreateThread/ExitThread, existing patterns
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — AttachAsync, GetCurrentThread, GetStackTraceAsync, GetLocalsAsync, LaunchAsync
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` — EnumerateThreads, SetAllThreadsDebugState, DebugActiveProcess, CorDebugExceptionCallbackType
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/Models.cs` — ExceptionEvent, StackFrameInfo models
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Mcp/DebuggerTools.cs` — current MCP tool signatures

### Secondary (HIGH confidence — project memory)
- `MEMORY.md` — confirmed: IMetaDataImport not available on Linux; VariableReader reads Exception fields via PE loop; `_message` field works for DivideByZeroException

---

## Metadata

**Confidence breakdown:**
- Exception info extraction: HIGH — pattern is direct extension of existing VariableReader.ReadObject; same approach used for Exception fields in Phase 6 tests
- Multi-thread inspection: HIGH — `EnumerateThreads` and `GetThread` are defined interfaces; pattern follows existing GetStackTraceAsync exactly
- Double-reporting guard: MEDIUM — v1/v2 ordering confirmed by ICorDebug design but not experimentally verified on .NET 10 specifically
- Attach completion: HIGH — `OnProcessCreated` callback is already used; TaskCompletionSource pattern mirrors LaunchAsync exactly
- THRD-03 (PauseAsync stops all threads): HIGH — `ICorDebugProcess.Stop(0)` suspends all managed threads per ICorDebug specification

**Research date:** 2026-02-23
**Valid until:** 2026-03-25 (stable ICorDebug API)
