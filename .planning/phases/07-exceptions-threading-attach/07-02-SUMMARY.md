---
phase: 07-exceptions-threading-attach
plan: 02
subsystem: debug-engine
tags: [icordebug, threading, multi-thread, stacktrace, variables, csharp]

# Dependency graph
requires:
  - phase: 07-exceptions-threading-attach
    plan: 01
    provides: exception event infrastructure (TryReadExceptionInfo, NotifyFirstChanceExceptions)
provides:
  - GetAllThreads() helper using celt=1 loop for safe COM interop thread enumeration
  - GetThreadById(uint) to retrieve a thread by OS thread ID
  - GetStackFramesForThread(ICorDebugThread) extracted helper for per-thread frame walking
  - GetStackTraceAsync(uint threadId=0) — optional thread targeting
  - GetLocalsAsync(uint threadId=0) — optional thread targeting
  - GetAllThreadStackTracesAsync() — all-threads stacktrace returning (ThreadId, Frames) list
  - debug_variables MCP tool with optional thread_id parameter
  - debug_stacktrace MCP tool with optional thread_id parameter (all-threads when 0)
affects: [07-03, future-multi-thread-debugging]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - celt=1 loop for ICorDebugThreadEnum.Next (avoids LPArray marshaling issues with source-generated COM)
    - Optional uint threadId=0 default to preserve backward-compatible single-thread behavior
    - Branch on thread_id!=0 in MCP tools to select single-thread vs all-threads path

key-files:
  created: []
  modified:
    - src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs
    - src/DebuggerNetMcp.Mcp/DebuggerTools.cs

key-decisions:
  - "GetAllThreads uses celt=1 loop (ICorDebugThreadEnum.Next with single-element array) — same pattern as chain/frame enumeration to avoid LPArray COM marshaling issues"
  - "GetStackFramesForThread extracted as private helper — reused by both GetStackTraceAsync and GetAllThreadStackTracesAsync, eliminating code duplication"
  - "debug_stacktrace with thread_id=0 returns {threads:[{threadId,frames},...]} (all-threads); with non-zero returns {thread_id, frames} (single thread)"
  - "Optional uint threadId=0 default preserves backward-compatible behavior — existing callers need no changes"

patterns-established:
  - "celt=1 enumeration: ICorDebugThreadEnum follows same pattern as chain/frame enumerators"
  - "All-threads vs single-thread branch: thread_id==0 sentinel means all-threads"

requirements-completed: [THRD-01, THRD-02, THRD-03]

# Metrics
duration: 3min
completed: 2026-02-23
---

# Phase 07 Plan 02: Threading — Multi-Thread Stacktrace and Variable Inspection Summary

**Optional thread_id on debug_variables/debug_stacktrace with celt=1 GetAllThreads helper, enabling per-thread locals and all-threads call stack in multi-thread debugging scenarios**

## Performance

- **Duration:** 3 min
- **Started:** 2026-02-23T14:12:41Z
- **Completed:** 2026-02-23T14:16:10Z
- **Tasks:** 2
- **Files modified:** 2

## Accomplishments

- Added `GetAllThreads()` private helper using celt=1 loop pattern (avoids LPArray COM interop marshaling issues)
- Added `GetThreadById(uint)` for targeted thread lookup via `ICorDebugProcess.GetThread`
- Extracted `GetStackFramesForThread(ICorDebugThread)` from `GetStackTraceAsync` body — reused by single-thread and all-threads paths
- Updated `GetStackTraceAsync(uint threadId=0)` and `GetLocalsAsync(uint threadId=0)` with optional thread targeting
- Added `GetAllThreadStackTracesAsync()` that enumerates all threads with celt=1 loop and returns `(ThreadId, Frames)` list
- Updated `debug_variables` MCP tool with optional `thread_id` parameter
- Updated `debug_stacktrace` MCP tool: `thread_id=0` returns `{threads:[...]}`, non-zero returns `{thread_id, frames:[...]}`

## Task Commits

Each task was committed atomically:

1. **Task 1: GetAllThreads helper + optional threadId in DotnetDebugger** - `a0fc2ed` (feat)
2. **Task 2: Update debug_variables and debug_stacktrace MCP tools with thread_id parameter** - `53edffb` (feat, committed as part of 07-01 plan execution)

## Files Created/Modified

- `src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` - Added GetAllThreads, GetThreadById, GetStackFramesForThread helpers; updated GetStackTraceAsync and GetLocalsAsync with optional threadId; added GetAllThreadStackTracesAsync
- `src/DebuggerNetMcp.Mcp/DebuggerTools.cs` - Updated debug_variables and debug_stacktrace with optional thread_id parameter and branching logic

## Decisions Made

- **celt=1 loop for thread enumeration**: `ICorDebugThreadEnum.Next(1, arr, out fetched)` avoids `[MarshalAs(LPArray, SizeParamIndex)]` issues in source-generated COM interop — consistent with existing chain/frame enumeration patterns.
- **thread_id=0 sentinel for all-threads path**: Natural default that preserves backward compatibility while enabling explicit thread targeting with any non-zero OS thread ID.
- **`GetStackFramesForThread` extraction**: Eliminates code duplication between `GetStackTraceAsync` and `GetAllThreadStackTracesAsync`.

## Deviations from Plan

None - plan executed exactly as written. Task 2 (DebuggerTools.cs changes) was already committed as part of the 07-01 plan execution that ran before this session; Task 1 (DotnetDebugger.cs) was committed fresh in this session.

## Issues Encountered

None.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- Threading API complete: GetAllThreads, GetAllThreadStackTracesAsync, optional threadId on GetLocalsAsync/GetStackTraceAsync
- debug_variables and debug_stacktrace both thread-aware
- Ready for Phase 07-03 (attach to running process and exception-handling scenarios)

---
*Phase: 07-exceptions-threading-attach*
*Completed: 2026-02-23*
