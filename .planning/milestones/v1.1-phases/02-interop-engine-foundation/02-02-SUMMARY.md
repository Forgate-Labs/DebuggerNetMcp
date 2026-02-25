---
phase: 02-interop-engine-foundation
plan: 02
subsystem: interop
tags: [com-interop, libdbgshim, icordebug, generated-com-interface, native-library]
dependency_graph:
  requires: []
  provides: [DbgShimInterop, ICorDebug, ICorDebugProcess, ICorDebugThread, ICorDebugFrame, ICorDebugILFrame, ICorDebugFunction, ICorDebugModule, ICorDebugValue, ICorDebugManagedCallback]
  affects: [02-03-PLAN.md, Phase 3 DotnetDebugger.cs]
tech_stack:
  added: [NativeLibrary, GeneratedComInterface, MarshalAs]
  patterns: [dynamic-library-loading, generated-com-interface, gc-lifetime-guard]
key_files:
  created:
    - src/DebuggerNetMcp.Core/Interop/DbgShimInterop.cs
    - src/DebuggerNetMcp.Core/Interop/ICorDebug.cs
  modified:
    - src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj
decisions:
  - "AllowUnsafeBlocks enabled in DebuggerNetMcp.Core.csproj — required by [GeneratedComInterface] source generator"
  - "char[] and object marshalling replaced with IntPtr in [GeneratedComInterface] methods — SYSLIB1051/SYSLIB1052 compliance"
  - "All 40 stub/core interfaces use real GUIDs from cordebug.idl, not placeholders — ensures vtable correctness"
metrics:
  duration: "~4 min"
  completed: "2026-02-22"
  tasks_completed: 2
  files_created: 2
  files_modified: 1
---

# Phase 02 Plan 02: COM Interop Layer (DbgShimInterop + ICorDebug) Summary

**One-liner:** Dynamic libdbgshim.so loading with 6-path candidate search + all 17 ICorDebug COM interfaces defined via [GeneratedComInterface] with authoritative GUIDs from cordebug.idl.

## What Was Built

### Task 1: DbgShimInterop.cs + AllowUnsafeBlocks (commit: bcf39cf)

Created `src/DebuggerNetMcp.Core/Interop/DbgShimInterop.cs` — the runtime bridge to libdbgshim.so:

- `Load(string? dbgShimPath)`: Searches 6 candidate groups via `NativeLibrary.TryLoad`, binds 4 delegates on success, throws `FileNotFoundException` listing all attempted paths on failure
- `BuildCandidateList`: Searches in order: explicit path → `DBGSHIM_PATH` env var → `DOTNET_ROOT/shared/Microsoft.NETCore.App` subdirs (newest first) → `/usr/share/dotnet/shared/...` subdirs → `NETCOREDBG_PATH` directory → `~/.local/lib/netcoredbg/libdbgshim.so` → `/usr/local/lib/netcoredbg/libdbgshim.so`
- `KeepAlive(RuntimeStartupCallback)`: GC lifetime guard — Phase 3 MUST call this before `RegisterForRuntimeStartup` to prevent the callback from being collected before native code fires it
- Bound delegates: `RegisterForRuntimeStartup`, `CreateProcessForLaunch`, `ResumeProcess`, `CloseResumeHandle`

### Task 2: ICorDebug.cs (commit: 94ba040)

Created `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` — all COM interfaces needed by Phase 3:

**17 core interfaces** (all with real GUIDs from cordebug.idl):
- `ICorDebug` — debugger root object (Initialize, SetManagedHandler, DebugActiveProcess, GetProcess)
- `ICorDebugController` — process/appdomain control base (Stop, Continue, EnumerateThreads)
- `ICorDebugProcess : ICorDebugController` — process access (GetID, GetThread, ReadMemory, WriteMemory)
- `ICorDebugThread` — thread state (GetID, GetActiveFrame, CreateStepper, SetDebugState)
- `ICorDebugFrame` — stack frame base (GetFunction, GetStackRange, GetCallee)
- `ICorDebugILFrame : ICorDebugFrame` — IL execution context (GetIP, GetLocalVariable, GetArgument)
- `ICorDebugFunction` — method metadata (GetModule, GetToken, GetILCode, CreateBreakpoint)
- `ICorDebugModule` — assembly module (GetName, GetFunctionFromToken, GetMetaDataInterface)
- `ICorDebugValue` — value base (GetType, GetSize, GetAddress)
- `ICorDebugGenericValue : ICorDebugValue` — blittable value (GetValue, SetValue)
- `ICorDebugStringValue : ICorDebugHeapValue` — string on heap (GetLength, GetString)
- `ICorDebugObjectValue : ICorDebugValue` — object instance (GetClass, GetFieldValue)
- `ICorDebugArrayValue : ICorDebugHeapValue` — array on heap (GetCount, GetRank, GetElement)
- `ICorDebugBreakpoint` — breakpoint activation (Activate, IsActive)
- `ICorDebugFunctionBreakpoint : ICorDebugBreakpoint` — function breakpoint (GetFunction, GetOffset)
- `ICorDebugStepper` — execution step control (Step, StepRange, StepOut, SetInterceptMask)
- `ICorDebugManagedCallback` — 26-method debug event sink (Breakpoint, StepComplete, LoadModule, etc.)
- `ICorDebugManagedCallback2` — extended events (Exception, ExceptionUnwind, MDANotification)

**Helper interfaces** (with real GUIDs): `ICorDebugHeapValue`, `ICorDebugReferenceValue`

**Stub interfaces** (with real GUIDs from IDL): `ICorDebugAppDomain`, `ICorDebugAssembly`, `ICorDebugChain`, `ICorDebugClass`, `ICorDebugContext`, `ICorDebugCode`, `ICorDebugEval`, `ICorDebugMDA`, `ICorDebugRegisterSet`, `ICorDebugErrorInfoEnum`, `ICorDebugObjectEnum`, `ICorDebugProcessEnum`, `ICorDebugThreadEnum`, `ICorDebugAppDomainEnum`, `ICorDebugChainEnum`, `ICorDebugValueEnum`, `ICorDebugModuleBreakpoint`, `ICorDebugValueBreakpoint`, `ICorDebugEditAndContinueSnapshot`, `ICorDebugUnmanagedCallback`

**8 enums:** `CorDebugThreadState`, `CorDebugUserState`, `CorDebugMappingResult`, `CorDebugStepReason`, `CorDebugIntercept`, `CorDebugUnmappedStop`, `CorDebugExceptionCallbackType`, `CorDebugExceptionUnwindCallbackType`

**Struct:** `COR_DEBUG_STEP_RANGE { startOffset, endOffset }`

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed [GeneratedComInterface] incompatible marshalling attributes**
- **Found during:** Task 2, first build attempt
- **Issue:** SYSLIB1051 — `char[]` with `[MarshalAs(UnmanagedType.LPArray)]` is not supported by source-generated COM without `DisableRuntimeMarshallingAttribute`. SYSLIB1052 — `[MarshalAs(UnmanagedType.LPStruct)]` on `Guid` and `[MarshalAs(UnmanagedType.IUnknown)]` on `object` are not supported by source-generated COM.
- **Fix:** Replaced `char[]` parameters with `IntPtr` in `ICorDebugModule.GetName`, `ICorDebugStringValue.GetString`. Replaced `[MarshalAs(UnmanagedType.LPStruct)] Guid` with `in Guid` and `[MarshalAs(UnmanagedType.IUnknown)] object` with `IntPtr` in `ICorDebugModule.GetMetaDataInterface`. Replaced `object` with `IntPtr` in `ICorDebugObjectValue.GetManagedCopy/SetFromManagedCopy`. Replaced `object` with `IntPtr` in `ICorDebugManagedCallback.UpdateModuleSymbols`.
- **Files modified:** `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs`
- **Commit:** 94ba040 (included in task commit)

**2. [Rule 2 - Decision] Used real GUIDs for all stub interfaces (deviation from plan's placeholder suggestion)**
- **Found during:** Task 2, design review
- **Issue:** Plan suggested placeholder GUIDs for stub interfaces. Since all real GUIDs were already available from cordebug.idl, using real GUIDs is strictly better — it prevents incorrect vtable lookups if native code ever queries these interfaces.
- **Fix:** Used authoritative GUIDs from cordebug.idl for all 40 interfaces in the file.

## Commits

| Task | Commit | Description |
|------|--------|-------------|
| Task 1 | bcf39cf | feat(02-02): add DbgShimInterop.cs with dynamic libdbgshim.so loading |
| Task 2 | 94ba040 | feat(02-02): add ICorDebug.cs with all 17 COM interface definitions |

## Self-Check: PASSED

- [x] `src/DebuggerNetMcp.Core/Interop/DbgShimInterop.cs` exists
- [x] `src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` exists
- [x] commit bcf39cf exists in git log
- [x] commit 94ba040 exists in git log
- [x] `dotnet build` exits 0 with 0 warnings, 0 errors
- [x] `grep -c "[GeneratedComInterface]"` returns 40 (>= 17)
- [x] `grep -c "[Guid("` returns 40 (>= 17)
- [x] `grep "AllowUnsafeBlocks"` shows `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`
- [x] `grep "NativeLibrary.TryLoad"` matches in DbgShimInterop.cs
- [x] `grep "netcoredbg"` confirms ~/.local/lib/netcoredbg candidate path is present
