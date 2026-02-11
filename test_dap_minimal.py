"""Minimal DAP test to debug the protocol flow."""

import asyncio
import json
import sys

sys.path.insert(0, "src")

from debugger_net_mcp.dap_client import DapClient, find_netcoredbg
from debugger_net_mcp.dotnet_utils import dotnet_build


async def main():
    # 1. Build
    print("Building...")
    output, dll = await dotnet_build("/tmp/debug-test/HelloDebug")
    if not dll:
        print(f"Build failed:\n{output}")
        return
    print(f"DLL: {dll}")

    # 2. Start netcoredbg
    dap = DapClient()
    await dap.start()
    print("netcoredbg started")

    # 3. Initialize
    print("Sending initialize...")
    resp = await dap.send_request("initialize", {
        "clientID": "test",
        "adapterID": "coreclr",
        "pathFormat": "path",
        "linesStartAt1": True,
        "columnsStartAt1": True,
    })
    print(f"Initialize OK")

    # 4. Wait for initialized event
    ev = await dap.wait_for_event("initialized", timeout=3.0)
    print(f"Initialized event: {'YES' if ev else 'NO'}")

    # 5. Set breakpoint BEFORE launch
    print("Setting breakpoint at line 3...")
    bp_resp = await dap.send_request("setBreakpoints", {
        "source": {"path": "/tmp/debug-test/HelloDebug/Program.cs"},
        "breakpoints": [{"line": 3}],
    })
    bps = bp_resp.get("body", {}).get("breakpoints", [])
    print(f"Breakpoint status: {bps}")

    # 6. Launch (as concurrent task - it may block until configurationDone)
    print("Sending launch (concurrent)...")
    launch_task = asyncio.create_task(dap.send_request("launch", {
        "program": dll,
        "cwd": str(__import__("pathlib").Path(dll).parent),
        "stopAtEntry": True,
    }))

    # 7. Give launch a moment to start
    await asyncio.sleep(0.5)

    # 8. Configuration done
    print("Sending configurationDone...")
    cd_resp = await dap.send_request("configurationDone")
    print(f"ConfigDone OK: {cd_resp.get('success')}")

    # 9. Wait for launch
    print("Waiting for launch response...")
    launch_resp = await asyncio.wait_for(launch_task, timeout=10.0)
    print(f"Launch OK: {launch_resp.get('success')}")

    # 10. Wait for stopped event
    print("Waiting for stopped event...")
    stopped = await dap.wait_for_event("stopped", timeout=10.0)
    if stopped:
        body = stopped.get("body", {})
        print(f"Stopped! reason={body.get('reason')} threadId={body.get('threadId')}")
    else:
        print("TIMEOUT - no stopped event")

    # 11. If stopped, get stacktrace
    if stopped:
        thread_id = stopped.get("body", {}).get("threadId", 1)
        st = await dap.send_request("stackTrace", {
            "threadId": thread_id,
            "startFrame": 0,
            "levels": 5,
        })
        frames = st.get("body", {}).get("stackFrames", [])
        for f in frames:
            src = f.get("source", {})
            print(f"  Frame: {f.get('name')} at {src.get('path')}:{f.get('line')}")

        # Get variables
        scopes = await dap.send_request("scopes", {"frameId": frames[0]["id"]})
        for s in scopes.get("body", {}).get("scopes", []):
            print(f"  Scope: {s.get('name')} ref={s.get('variablesReference')}")
            vars_resp = await dap.send_request("variables", {
                "variablesReference": s["variablesReference"],
            })
            for v in vars_resp.get("body", {}).get("variables", []):
                print(f"    {v['name']} = {v.get('value')} ({v.get('type')})")

    # 12. Disconnect
    print("\nDisconnecting...")
    await dap.send_request("disconnect", {"terminateDebuggee": True})
    await dap.stop()
    print("Done!")


asyncio.run(main())
