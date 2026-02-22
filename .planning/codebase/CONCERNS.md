# Codebase Concerns

**Analysis Date:** 2025-02-22

## Tech Debt

### Assertions Instead of Proper Error Handling

**Issue:** Code uses `assert` statements for runtime validation instead of raising exceptions.

**Files:**
- `src/debugger_net_mcp/dap_client.py` lines 212, 217, 232

**Impact:** Assertions can be disabled with Python's `-O` flag, causing silent failures in production. Process stdin/stdout could be None but code doesn't handle it gracefully.

**Fix approach:** Replace assertions with explicit checks and raise `RuntimeError` or `AssertionError` with descriptive messages:
```python
# Current (line 212):
assert self._process and self._process.stdin
self._process.stdin.write(header + body)

# Should be:
if not self._process or not self._process.stdin:
    raise RuntimeError("DAP process stdin is not available")
self._process.stdin.write(header + body)
```

---

### Bare Except Handler with Swallowed State

**Issue:** `_reader_loop()` in `dap_client.py` (line 226-228) catches all exceptions and logs them, but may leave reader in inconsistent state.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 226-228

**Impact:** If reader crashes unexpectedly, pending requests never get responses. Callers hang forever waiting for futures that will never complete.

**Fix approach:** On exception, fail all pending futures:
```python
except Exception:
    if not self._closed:
        logger.exception("DAP reader loop error")
        # Fail pending requests so callers don't hang
        for fut in self._pending.values():
            if not fut.done():
                fut.set_exception(RuntimeError("DAP reader crashed"))
        self._pending.clear()
```

---

### Bare Except in disconnect()

**Issue:** `disconnect()` in `session.py` (line 379) catches all exceptions silently without logging.

**Files:** `src/debugger_net_mcp/session.py` line 379

**Impact:** Silent failures when disconnect fails (e.g., netcoredbg already crashed). Errors go unnoticed.

**Fix approach:** Add logging:
```python
try:
    if self._dap.is_running:
        await self._dap.send_request("disconnect", {"terminateDebuggee": True})
except Exception as e:
    logger.warning("disconnect request failed: %s", e)
```

---

## Known Bugs

### Race Condition in netcoredbg (Kernel >= 6.12)

**Issue:** netcoredbg crashes with SIGSEGV during `configurationDone` due to race in libdbgshim.so debug pipe setup.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 15-41, 123-128

**Trigger:** Launch debugger on Linux kernel >= 6.12 without strace workaround.

**Current mitigation:** Wraps netcoredbg with `strace -f -e trace=none -o /dev/null`. The ptrace overhead prevents the race. Auto-detected on kernel >= 6.12.

**Workaround:** Set `DEBUGGER_NET_MCP_NO_STRACE=1` to disable (only safe on kernel < 6.12).

**Root cause:** CoreCLR thread created via `clone3` has race with libdbgshim callback timing. Not a bug in MCP server but unavoidable with affected kernel/netcoredbg versions.

---

### Session Singleton Not Thread-Safe

**Issue:** `_session` global in `session.py` (lines 557-566) is created lazily without locking.

**Files:** `src/debugger_net_mcp/session.py` lines 557-566

**Impact:** If `get_session()` called from multiple async tasks simultaneously, could create multiple session instances.

**Workaround:** MCP server runs single async event loop, so concurrent calls are sequential. Not a practical issue in current architecture.

**Fix approach:** Use `asyncio.Lock` or lazy initialization at startup:
```python
_session: DebugSession | None = None
_session_lock = asyncio.Lock()

async def get_session() -> DebugSession:
    global _session
    if _session is None:
        _session = DebugSession()
    return _session
```

---

### No Handling of Stale DAP State Between Sessions

**Issue:** `_dap.start()` clears pending requests and seq counter, but if a previous session crashed, queued events might exist.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 89-92

**Impact:** Old events from crashed session could be misrouted to new session if queue isn't drained.

**Workaround:** Events are keyed by name (not request seq), so stale events get dropped naturally when session reconnects.

**Fix approach:** Clear event queues on start:
```python
async def start(self) -> None:
    # Clear stale state from any previous session
    self._seq = 1
    self._pending.clear()
    self._event_queues.clear()  # Already done at line 92
```

This is actually already implemented correctly at line 92. No fix needed.

---

## Security Considerations

### PATH Traversal Risk in File Operations

**Issue:** `set_breakpoint()` and `remove_breakpoints()` accept user-provided `file_path` directly without validation.

**Files:** `src/debugger_net_mcp/session.py` lines 163-184

**Risk:** Attacker could set breakpoints in arbitrary files by passing `../../../etc/passwd` style paths. DAP would reject invalid files, but no validation happens client-side.

**Current mitigation:** DAP adapter validates file existence. Malformed paths fail at debugger level.

**Recommendations:**
- Validate file paths are absolute and exist before sending to DAP
- Add symlink resolution to prevent directory escape
- Log all file access attempts

---

### No Input Validation on Expression Evaluation

**Issue:** `evaluate()` accepts arbitrary C# expressions sent directly to netcoredbg without sanitization.

**Files:** `src/debugger_net_mcp/session.py` lines 339-367

**Risk:** User could evaluate expressions with side effects (e.g., `Process.Kill()`, `File.Delete()`). Expression runs with debuggee's privileges.

**Current mitigation:** Expression executes in debuggee's context, so impact limited to debuggee's own process. No reflection/remoting exploits possible.

**Recommendations:**
- Document that expressions execute with debuggee's privileges
- Consider adding expression whitelist or AST validation if needed
- Add audit logging for evaluated expressions

---

### Environment Variables Exposed via Process Inspection

**Issue:** `_dap.start()` copies `os.environ` to subprocess env, exposing all parent process env vars to netcoredbg.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 97-119

**Risk:** Secrets in parent process env (API keys, tokens) become visible to debugged .NET process and netcoredbg.

**Current mitigation:** None. Inherits parent environment by design.

**Recommendations:**
- Document that debugging exposes parent env vars to debuggee
- Consider filtering sensitive vars (AWS_*, OPENAI_*, etc.)
- Provide option to whitelist only necessary vars

---

## Performance Bottlenecks

### Unbounded Output Buffer

**Issue:** `_output_lines` in `session.py` is a `deque(maxlen=MAX_OUTPUT_LINES)` capped at 200 lines.

**Files:** `src/debugger_net_mcp/session.py` lines 14, 32

**Problem:** Once buffer fills, old output is silently dropped. Long-running programs lose early output.

**Impact:** User can only see last 200 lines of output. Earlier log messages are lost.

**Improvement path:**
- Increase `MAX_OUTPUT_LINES` to 1000 or make configurable
- Or store to file and provide offset-based retrieval:
```python
# In session.py
async def get_output(self, offset: int = 0, limit: int = 100) -> list[str]:
    """Get output lines with pagination."""
    return list(self._output_lines)[offset:offset+limit]
```

---

### No Timeout on DAP Request Waiting

**Issue:** `send_request()` waits indefinitely for response if netcoredbg never replies.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 167-187

**Impact:** Caller blocks forever if DAP adapter hangs. MCP server becomes unresponsive.

**Improvement path:** Add timeout wrapper:
```python
async def send_request(self, command: str, arguments: dict | None = None, timeout: float = 30.0) -> dict:
    """Send a DAP request with timeout."""
    if not self.is_running:
        raise ConnectionError("netcoredbg is not running")

    seq = self._seq
    self._seq += 1
    msg = {"seq": seq, "type": "request", "command": command}
    if arguments:
        msg["arguments"] = arguments

    future: asyncio.Future[dict] = asyncio.get_event_loop().create_future()
    self._pending[seq] = future

    self._write_message(msg)
    try:
        return await asyncio.wait_for(future, timeout=timeout)
    except asyncio.TimeoutError:
        self._pending.pop(seq, None)
        raise RuntimeError(f"DAP request '{command}' timed out after {timeout}s")
```

---

### Synchronous JSON Parsing in Async Loop

**Issue:** `_read_message()` calls `json.loads()` synchronously in async reader loop (line 251).

**Files:** `src/debugger_net_mcp/dap_client.py` line 251

**Impact:** Large JSON payloads block the event loop. Negligible for typical DAP messages (usually < 10KB).

**Improvement path:** Use `asyncio.to_thread()` for parsing large responses (unlikely to matter in practice).

---

## Fragile Areas

### State Machine Transitions Not Fully Validated

**Issue:** `_require_state()` validates current state, but doesn't prevent invalid transitions (e.g., STOPPED → IDLE).

**Files:** `src/debugger_net_mcp/session.py` lines 48-53

**Why fragile:** Methods can only check what states are *allowed*, not *expected*. Race between state check and operation.

**Safe modification approach:**
1. Always call `_require_state()` at method start
2. Change state atomically before async operations
3. Test state transitions exhaustively

**Test coverage:** Integration test (`test_integration.py`) exercises happy path but not state machine edge cases (e.g., double-launch, disconnect-while-stepping).

---

### Event Ordering Assumptions

**Issue:** Code assumes `initialized` event arrives before `stopped` event (lines 96, 149).

**Files:** `src/debugger_net_mcp/session.py` lines 96, 149

**Why fragile:** DAP spec doesn't guarantee event ordering. Different debuggers/netcoredbg versions may reorder events.

**Safe modification:** Buffer events until `initialized` received:
```python
self._initialized = asyncio.Event()

def _dispatch(self, msg: dict) -> None:
    if msg.get("event") == "initialized":
        self._initialized.set()
    # ... normal dispatch
```

**Test coverage:** No test for event reordering scenarios.

---

### No Cleanup on Exception in launch()

**Issue:** If `dotnet_build()` fails or `_dap.start()` throws, session state set to INITIALIZING but no cleanup.

**Files:** `src/debugger_net_mcp/session.py` lines 57-127

**Why fragile:** Partial initialization leaves resources open. Next launch attempt sees INITIALIZING state and fails.

**Safe modification:** Use try-finally:
```python
async def launch(self, project_path: str, stop_at_entry: bool = True, args: list[str] | None = None) -> dict:
    self._require_state(SessionState.IDLE, SessionState.TERMINATED)
    self._reset()
    self._state = SessionState.INITIALIZING

    try:
        build_output, dll_path = await dotnet_build(project_path)
        if dll_path is None:
            return {"success": False, "error": "Build failed", "output": build_output}

        await self._dap.start()
        # ... rest of launch
    except Exception:
        await self._cleanup()
        raise
```

**Test coverage:** No test for launch failures.

---

## Scaling Limits

### Single Session per Server Instance

**Issue:** Global `_session` singleton means only one debug session per server process.

**Files:** `src/debugger_net_mcp/session.py` lines 557-566

**Current capacity:** 1 debug session at a time.

**Limit:** Can't debug multiple projects simultaneously from one MCP server instance.

**Scaling path:**
1. Use session ID instead of global: `sessions: dict[str, DebugSession] = {}`
2. Accept `session_id` parameter in all tools
3. Implement session cleanup on disconnect

This is architectural decision, not a bug. MCP server per Claude Code instance is the design.

---

### Unbounded Pending Request Dictionary

**Issue:** `_pending` dict in `dap_client.py` accumulates failed requests if responses never arrive.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 77, 184, 259, 270

**Impact:** Memory leak over long sessions with many failed requests.

**Scaling path:** Combine with timeout fix above to auto-cleanup stale pending requests:
```python
async def send_request(self, command: str, arguments: dict | None = None, timeout: float = 30.0) -> dict:
    # ... existing code ...
    try:
        return await asyncio.wait_for(future, timeout=timeout)
    except asyncio.TimeoutError:
        self._pending.pop(seq, None)  # Clean up
        raise RuntimeError(f"DAP request '{command}' timed out")
```

---

## Dependencies at Risk

### No Pinned Dependency Versions

**Issue:** `pyproject.toml` specifies `mcp[cli]>=1.2.0` without upper bound.

**Files:** `pyproject.toml` line 11

**Risk:** Minor MCP version bumps could introduce breaking changes. No reproducible builds without lock file.

**Current status:** `uv.lock` exists (added in commit d818f87), so reproducibility is handled.

**Migration plan:** Continue using `uv.lock` for reproducible installs. Add pre-release pin if MCP releases breaking changes.

---

### netcoredbg Compatibility Unclear

**Issue:** Code assumes netcoredbg 3.1.x but doesn't validate version.

**Files:** `src/debugger_net_mcp/dap_client.py` lines 51-68

**Risk:** Older netcoredbg (< 3.0) may not support DAP. Newer versions may change protocol.

**Current mitigation:** README documents required version. Error message mentions version mismatch.

**Recommendations:**
- Add version check: `netcoredbg --version` on startup
- Store version in session state
- Warn if version < 3.0 or > 4.0

---

## Missing Critical Features

### No Conditional Breakpoint Verification

**Issue:** `set_breakpoint()` accepts condition parameter but doesn't validate C# syntax.

**Files:** `src/debugger_net_mcp/session.py` lines 163-184

**Problem:** Invalid conditions sent to netcoredbg. Debugger silently ignores bad conditions.

**Blocks:** Users can't detect if breakpoint condition is invalid until runtime.

**Recommendation:** Parse condition syntax or at least log when breakpoint reports unverified.

---

### No Exception Breakpoints

**Issue:** Can't set "break on all exceptions" or "break on specific exception type".

**Files:** `src/debugger_net_mcp/session.py` (not implemented)

**Problem:** Common debugging need. Only line-based breakpoints supported.

**Blocks:** Can't catch unhandled exceptions automatically.

**Recommendation:** Add `debug_set_exception_breakpoint(exception_type: str)` tool using DAP `setExceptionBreakpoints`.

---

### No Call Stack Filtering

**Issue:** `stacktrace()` returns all frames including framework internals.

**Files:** `src/debugger_net_mcp/session.py` lines 248-269

**Problem:** User sees 50+ frames of BCL noise. Hard to find user code.

**Recommendation:** Add parameter to hide framework frames:
```python
async def stacktrace(self, levels: int = 20, skip_framework: bool = True) -> dict:
    # Filter frames with source path containing ".NET" or "System"
```

---

### No Watch Expressions

**Issue:** Can only evaluate expressions on-demand. No persistent watch list.

**Files:** `src/debugger_net_mcp/session.py` (not implemented)

**Problem:** Have to re-evaluate same expressions repeatedly.

**Recommendation:** Add watch API:
```python
async def add_watch(self, expression: str) -> dict:
    """Add expression to watch list."""

async def get_watches(self) -> dict:
    """Get all watched expressions and their current values."""
```

---

## Test Coverage Gaps

### No Tests for Error Paths

**Issue:** Integration test (`test_integration.py`) only exercises happy path. No error scenarios tested.

**Files:** `test_integration.py` lines 32-122

**What's not tested:**
- Build failure handling
- netcoredbg not found
- Breakpoint on non-existent file
- Invalid expression evaluation
- Process crash during debugging
- Timeout scenarios

**Risk:** Errors produce unclear messages or hang server.

**Priority:** Medium — happy path works, but users will hit these errors.

---

### No Protocol Compliance Tests

**Issue:** No formal DAP protocol validation. Tests assume netcoredbg response format.

**Files:** `test_dap_protocol.py` (minimal coverage)

**What's not tested:**
- Message framing edge cases (malformed Content-Length)
- Large payloads (> 1MB)
- Rapid fire requests/responses
- Concurrent requests
- Event ordering

**Risk:** Protocol bugs emerge unpredictably under load.

**Priority:** Low — DAP is relatively simple, netcoredbg is well-behaved.

---

### No Async Concurrency Tests

**Issue:** All tests are sequential. No tests for concurrent operations (e.g., pause while stepping).

**Files:** `test_integration.py` (sequential only)

**What's not tested:**
- `pause()` called while `continue()` pending
- `step_over()` interrupted by exception
- Multiple `wait_for_event()` calls racing

**Risk:** Race conditions in event dispatch discovered in production.

**Priority:** Low — MCP server sequential per call, but good for robustness.

---

### No Negative Tests for State Validation

**Issue:** `_require_state()` not tested with invalid state transitions.

**Files:** `src/debugger_net_mcp/session.py` lines 48-53

**What's not tested:**
- Call `continue()` without launching
- Call `stacktrace()` while running
- Double-launch
- Operations after disconnect

**Risk:** Unclear error messages.

**Priority:** Medium — state machine is core to debugging flow.

---

## Summary of Priority Fixes

**High Priority (blocking/critical):**
1. Replace asserts with proper exception handling (lines 212, 217, 232)
2. Add exception cleanup in `_reader_loop()` to fail pending requests
3. Add logging to `disconnect()` exception handler

**Medium Priority (important):**
4. Add timeout to `send_request()` to prevent hanging
5. Fix exception cleanup in `launch()` try-finally
6. Add state machine negative tests
7. Add error path tests (build failure, missing netcoredbg, etc.)

**Low Priority (improvements):**
8. Increase `MAX_OUTPUT_LINES` or implement pagination
9. Add DAP request timeout validation
10. Add exception/watch breakpoint features
11. Add call stack filtering option

---

*Concerns audit: 2025-02-22*
