"""Integration test: exercises the full debug flow via Python session classes.

Tests: set_breakpoint → launch → step_over → variables → continue →
       evaluate → stacktrace → disconnect
"""

import asyncio
import sys

sys.path.insert(0, "src")

from debugger_net_mcp.session import DebugSession

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
    suffix = f" — {detail}" if detail else ""
    print(f"  [{tag}] {name}{suffix}")


async def main() -> int:
    session = DebugSession()

    # 1. Set breakpoint before launch (deferred)
    print("\n--- 1. Set breakpoint at line 3 (before launch) ---")
    r = await session.set_breakpoint(SOURCE, 3)
    report("set_breakpoint (deferred)", r.get("status") == "pending", str(r))

    # 2. Launch with stopAtEntry
    print("\n--- 2. Launch project (stopAtEntry=True) ---")
    r = await session.launch(PROJECT, stop_at_entry=True)
    ok = r.get("success", False)
    report("launch", ok, f"state={r.get('state')} reason={r.get('reason')}")
    if not ok:
        print(f"  ABORT: Launch failed: {r}")
        return 1

    # 3. Status check
    print("\n--- 3. Status ---")
    s = session.status()
    report("status", s["state"] == "stopped", f"state={s['state']}")

    # 4. Step over
    print("\n--- 4. Step over ---")
    r = await session.step_over()
    report("step_over", r.get("stopped", False), f"location={r.get('location')}")

    # 5. Variables
    print("\n--- 5. Variables ---")
    r = await session.variables()
    var_names = [v["name"] for v in r.get("variables", [])]
    report("variables", len(var_names) > 0, f"names={var_names}")

    # 6. Continue to breakpoint at line 3
    print("\n--- 6. Continue to breakpoint (line 3) ---")
    r = await session.continue_execution(timeout=10)
    report("continue→breakpoint", r.get("stopped", False), f"reason={r.get('reason')}")

    # 7. Evaluate expression
    print("\n--- 7. Evaluate 'greeting' ---")
    r = await session.evaluate("greeting")
    report("evaluate", "result" in r and r["result"] != "", f"result={r.get('result')}")

    # 8. Evaluate name variable
    print("\n--- 8. Evaluate 'name' ---")
    try:
        r = await session.evaluate("name")
        report("evaluate(name)", "result" in r and r["result"] != "", f"result={r.get('result')}")
    except RuntimeError as e:
        report("evaluate(name)", False, f"error={e}")

    # 9. Set breakpoint at line 9 (inside loop: sum += n)
    print("\n--- 9. Set breakpoint at line 9 (loop body) ---")
    r = await session.set_breakpoint(SOURCE, 9)
    report("set_breakpoint (live)", "breakpoints" in r or "status" in r, str(r))

    # 10. Continue to loop breakpoint
    print("\n--- 10. Continue to loop breakpoint ---")
    r = await session.continue_execution(timeout=10)
    report("continue→loop", r.get("stopped", False), f"reason={r.get('reason')} location={r.get('location')}")

    # 11. Variables in loop context
    print("\n--- 11. Variables in loop ---")
    r = await session.variables()
    var_names = [v["name"] for v in r.get("variables", [])]
    report("variables (loop)", "sum" in var_names or "n" in var_names, f"names={var_names}")

    # 12. Evaluate in loop context
    print("\n--- 12. Evaluate 'sum + n' ---")
    try:
        r = await session.evaluate("sum + n")
        report("evaluate(sum+n)", "result" in r, f"result={r.get('result')}")
    except RuntimeError as e:
        report("evaluate(sum+n)", False, f"error={e}")

    # 13. Stacktrace
    print("\n--- 13. Stacktrace ---")
    r = await session.stacktrace()
    frames = r.get("frames", [])
    report("stacktrace", len(frames) > 0, f"frames={len(frames)}, top={frames[0] if frames else '?'}")

    # 14. Disconnect
    print("\n--- 14. Disconnect ---")
    r = await session.disconnect()
    report("disconnect", r.get("status") == "disconnected", str(r))

    print(f"\n{'=' * 50}")
    print(f"Results: {passed} passed, {failed} failed")
    print(f"{'=' * 50}")

    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(asyncio.run(main()))
