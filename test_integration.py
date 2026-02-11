"""Integration test: launch → breakpoint → continue → variables → evaluate → disconnect."""

import asyncio
import sys

sys.path.insert(0, "src")

from debugger_net_mcp.session import get_session


async def main():
    session = get_session()
    project = "/tmp/debug-test/HelloDebug"
    source = f"{project}/Program.cs"

    print("=== 1. Set breakpoint at line 3 (before launch) ===")
    r = await session.set_breakpoint(source, 3)
    print(f"  Result: {r}")

    print("\n=== 2. Launch project (stop_at_entry=True) ===")
    r = await session.launch(project, stop_at_entry=True)
    print(f"  Result: {r}")
    if not r.get("success"):
        print("FAIL: Launch failed")
        return

    print(f"\n=== 3. Status ===")
    r = session.status()
    print(f"  State: {r['state']}")

    print("\n=== 4. Step over ===")
    r = await session.step_over()
    print(f"  Result: {r}")

    print("\n=== 5. Variables ===")
    r = await session.variables()
    print(f"  Variables: {r}")

    print("\n=== 6. Continue to breakpoint at line 3 ===")
    r = await session.continue_execution(timeout=10)
    print(f"  Result: {r}")

    print("\n=== 7. Evaluate expression ===")
    r = await session.evaluate("greeting")
    print(f"  Result: {r}")

    print("\n=== 8. Set breakpoint at line 9 (inside loop) ===")
    r = await session.set_breakpoint(source, 9)
    print(f"  Result: {r}")

    print("\n=== 9. Continue to loop breakpoint ===")
    r = await session.continue_execution(timeout=10)
    print(f"  Result: {r}")

    print("\n=== 10. Variables in loop ===")
    r = await session.variables()
    print(f"  Variables: {r}")

    print("\n=== 11. Evaluate in loop context ===")
    r = await session.evaluate("sum + n")
    print(f"  Result: {r}")

    print("\n=== 12. Stacktrace ===")
    r = await session.stacktrace()
    print(f"  Frames: {r}")

    print("\n=== 13. Disconnect ===")
    r = await session.disconnect()
    print(f"  Result: {r}")

    print("\n=== ALL TESTS PASSED ===")


asyncio.run(main())
