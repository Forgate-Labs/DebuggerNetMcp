# Testing Patterns

**Analysis Date:** 2026-02-22

## Test Framework

**Runner:**
- `asyncio` for async test execution (no pytest, unittest, or vitest)
- Raw async test scripts run via `python test_*.py` or `asyncio.run(main())`
- Exit codes: 0 for success, 1 for failure

**Assertion Library:**
- `assert` statements for protocol validation: `assert resp["success"], f"initialize failed: {resp}"`
- Manual result checking with helper functions: `report(name, ok, detail)` in integration tests

**Run Commands:**
```bash
python test_integration.py          # Full debug session flow
python test_dap_protocol.py         # Protocol order testing (Order A vs B)
python test_dap_minimal.py          # Minimal DAP workflow
python test_dap_raw.py              # Raw protocol with vsdbg-ui
```

## Test File Organization

**Location:**
- Root level (co-located with `src/` and `pyproject.toml`)
- Files: `test_dap_minimal.py`, `test_dap_protocol.py`, `test_dap_raw.py`, `test_integration.py`

**Naming:**
- `test_` prefix followed by focus: `test_dap_minimal.py`, `test_integration.py`
- Test functions use `async def` with descriptive names: `async def test_order_a()`, `async def main()`

**Structure:**
- No class-based test organization (functions directly in modules)
- Each test file has a `main()` function that orchestrates the test flow
- Entry point: `if __name__ == "__main__": sys.exit(asyncio.run(main()))`

## Test Structure

**Suite Organization:**
```python
async def main() -> int:
    session = DebugSession()

    # 1. Setup phase
    print("\n--- 1. Set breakpoint at line 3 (before launch) ---")
    r = await session.set_breakpoint(SOURCE, 3)
    report("set_breakpoint (deferred)", r.get("status") == "pending", str(r))

    # 2. Action phase
    print("\n--- 2. Launch project (stopAtEntry=True) ---")
    r = await session.launch(PROJECT, stop_at_entry=True)
    ok = r.get("success", False)
    report("launch", ok, f"state={r.get('state')} reason={r.get('reason')}")

    # 3. Assertion phase
    if not ok:
        print(f"  ABORT: Launch failed: {r}")
        return 1

    return 0 if failed == 0 else 1
```

**Patterns:**

**Setup Phase:**
```python
async def main():
    session = DebugSession()
    # or
    client = DapRawClient()
    await client.start()
```

**Action Phase (await operations):**
```python
r = await session.launch(PROJECT, stop_at_entry=True)
r = await session.set_breakpoint(SOURCE, 3)
r = await session.continue_execution(timeout=10)
```

**Assertion Phase (manual checks):**
```python
ok = r.get("success", False)
report("launch", ok, f"state={r.get('state')} reason={r.get('reason')}")

if not ok:
    print(f"  ABORT: Launch failed: {r}")
    return 1
```

**Cleanup Phase:**
```python
try:
    await session.disconnect()
finally:
    await client.stop()
```

## Mocking

**Framework:** No mocking library (unittest.mock, pytest-mock, etc.)

**Patterns:**
- Real integration tests using actual netcoredbg process: `await asyncio.create_subprocess_exec(*cmd, ...)`
- Real async subprocess communication with DAP protocol
- No mock objects or patches (tests run against live system)

**What to Mock:**
- Not applicable for this codebase. All tests use real dependencies (netcoredbg, .NET runtime)

**What NOT to Mock:**
- External process (netcoredbg) - tests depend on this
- Filesystem (tests build actual .NET projects)
- Subprocess I/O (protocol compliance is critical)

## Fixtures and Factories

**Test Data:**
- Hardcoded project path: `PROJECT = "/tmp/debug-test/HelloDebug"` in test files
- Source file: `SOURCE = f"{PROJECT}/Program.cs"` (refers to actual test C# project)
- Preset breakpoint line: `BP_LINE = 6` (in test_dap_protocol.py)
- DLL path: `DLL_PATH = f"{PROJECT}/bin/Debug/net9.0/HelloDebug.dll"`

**Example from `test_integration.py`:**
```python
PROJECT = "/tmp/debug-test/HelloDebug"
SOURCE = f"{PROJECT}/Program.cs"

passed = 0
failed = 0

def report(name: str, ok: bool, detail: str = "") -> None:
    global passed, failed
    tag = "PASS" if ok else "FAIL"
    if ok:
        passed += 1
    else:
        failed += 1
    print(f"  [{tag}] {name}{suffix}")
```

**Location:**
- Module-level constants at top of test files
- No separate fixtures directory
- Test project checked into `/tmp/debug-test/HelloDebug/` (expected to exist)

## Coverage

**Requirements:** No coverage target or enforcement detected (no `coverage`, `pytest-cov` config)

**View Coverage:** Not applicable (no coverage tool configured)

**Implied Coverage:**
- Full end-to-end workflows covered: launch → breakpoint → step → variables → evaluate → disconnect
- Protocol edge cases tested: Order A vs Order B DAP initialization
- Integration between layers: session → dap_client → netcoredbg subprocess

## Test Types

**Integration Tests:**
- `test_integration.py` - Full debug lifecycle via `DebugSession` class
  - Covers: launch, breakpoint (pre-launch and live), step_over, variables, continue, evaluate, stacktrace, disconnect
  - Validates session state transitions: IDLE → INITIALIZING → RUNNING → STOPPED → RUNNING → ...

**Protocol Tests:**
- `test_dap_protocol.py` - Two DAP initialization orderings
  - Tests Order A (standard DAP spec): initialize → launch → initialized → setBreakpoints → configurationDone → stopped
  - Tests Order B (netcoredbg-specific): initialize → initialized → setBreakpoints → configurationDone → launch → stopped
  - Validates complete session lifecycle: threads, stackTrace, scopes, variables, evaluate, continue, disconnect

**Minimal Tests:**
- `test_dap_minimal.py` - Focused DAP flow with actual `DapClient`
  - Covers: initialize, launch, breakpoint, stopped event, stacktrace, scopes, variables
  - Tests concurrent execution: launch as task while configurationDone is sent separately

**Raw Protocol Tests:**
- `test_dap_raw.py` - Low-level protocol with vsdbg-ui (reference implementation)
  - Tests raw Content-Length framing
  - Validates all DAP operations: threads, stackTrace, scopes, variables, continue
  - Provides stderr logging for debugging adapter issues

**Unit Tests:** None found. No small isolated unit tests.

## Common Patterns

**Async Testing:**
```python
async def main() -> int:
    session = DebugSession()

    # Await async operations
    r = await session.launch(PROJECT, stop_at_entry=True)

    # Wait for events with timeout
    stopped = await session._dap.wait_for_event("stopped", timeout=10.0)

    # Handle cancellation
    try:
        await asyncio.wait_for(proc.wait(), timeout=5)
    except asyncio.TimeoutError:
        proc.kill()
```

**Error Testing:**
```python
# Protocol error handling
try:
    r = await session.evaluate("name")
    report("evaluate(name)", "result" in r and r["result"] != "", f"result={r.get('result')}")
except RuntimeError as e:
    report("evaluate(name)", False, f"error={e}")

# Timeout handling
stopped = await self._dap.wait_for_event("stopped", timeout=10.0)
if stopped is None:
    return False  # Timeout occurred
```

**State Validation in Tests:**
```python
# Check state transitions
s = session.status()
report("status", s["state"] == "stopped", f"state={s['state']}")

# Validate response structure
r = await session.variables()
var_names = [v["name"] for v in r.get("variables", [])]
report("variables", len(var_names) > 0, f"names={var_names}")
```

**Concurrent Operations:**
```python
# Launch as background task while sending configuration
launch_task = asyncio.create_task(dap.send_request("launch", {...}))
await asyncio.sleep(0.5)
cd_resp = await dap.send_request("configurationDone")
launch_resp = await asyncio.wait_for(launch_task, timeout=10.0)
```

## Test Data Structure

**Response Validation Pattern:**
```python
r = await session.launch(PROJECT, stop_at_entry=True)
ok = r.get("success", False)
if ok:
    return {
        "success": True,
        "state": self._state.value,
        "reason": self._stop_reason,
        "location": self._stop_location,
    }
else:
    return {
        "success": False,
        "error": "Build failed",
        "output": build_output
    }
```

**DAP Message Structure:**
```python
msg = {
    "seq": seq,
    "type": "request",
    "command": command,
    "arguments": arguments
}
body = json.dumps(msg).encode("utf-8")
header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
```

## Test Execution Environment

**Prerequisites:**
- `.NET` runtime (8.0+) with `dotnet` command available
- `netcoredbg` binary at `~/.local/bin/netcoredbg` or via `NETCOREDBG_PATH`
- `libdbgshim.so` alongside netcoredbg
- Test project at `/tmp/debug-test/HelloDebug/` (must exist and be buildable)
- On kernel >= 6.12: `strace` command available for workaround

**Environment Variables:**
- `NETCOREDBG_PATH`: Override netcoredbg location
- `DOTNET_ROOT`: Override .NET root directory
- `LD_LIBRARY_PATH`: For libdbgshim.so discovery
- `DEBUGGER_NET_MCP_NO_STRACE`: Disable strace workaround (set to "1")

## Test Result Reporting

**Integration Test Format:**
```
--- 1. Set breakpoint at line 3 (before launch) ---
  [PASS] set_breakpoint (deferred) — {'status': 'pending', ...}

--- 2. Launch project (stopAtEntry=True) ---
  [PASS] launch — state=stopped reason=entry

==================================================
Results: 14 passed, 0 failed
==================================================
```

**Protocol Test Format:**
```
============================================================
Testing ORDER A (standard DAP spec)
============================================================
  [PASS] initialize → capabilities received
  [PASS] launch → accepted
  [PASS] initialized event received
  ...
  ORDER A: ALL TESTS PASSED ✓

============================================================
RESULTS:
  Order A (standard DAP): PASS
  Order B (netcoredbg-specific): PASS
============================================================
```

---

*Testing analysis: 2026-02-22*
