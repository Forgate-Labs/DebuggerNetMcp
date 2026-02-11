"""Raw DAP test with vsdbg-ui (handles handshake automatically)."""

import asyncio
import json
import sys
from pathlib import Path

sys.path.insert(0, "src")
from debugger_net_mcp.dotnet_utils import dotnet_build

VSDBG_UI = "/home/eduardo/.vscode/extensions/ms-dotnettools.csharp-2.120.3-linux-x64/.debugger/vsdbg-ui"


async def write_dap(stdin, seq: int, command: str, arguments: dict | None = None):
    msg = {"seq": seq, "type": "request", "command": command}
    if arguments:
        msg["arguments"] = arguments
    body = json.dumps(msg).encode("utf-8")
    header = f"Content-Length: {len(body)}\r\n\r\n".encode("ascii")
    stdin.write(header + body)
    await stdin.drain()
    print(f">>> SENT seq={seq} cmd={command}")


async def read_dap(stdout, timeout=10.0) -> dict | None:
    content_length = None
    while True:
        line = await asyncio.wait_for(stdout.readline(), timeout=timeout)
        if not line:
            return None
        line_str = line.decode("ascii", errors="replace").strip()
        if not line_str:
            if content_length is not None:
                break
            continue
        if line_str.startswith("Content-Length:"):
            content_length = int(line_str.split(":")[1].strip())

    body = await asyncio.wait_for(stdout.readexactly(content_length), timeout=timeout)
    msg = json.loads(body)
    msg_type = msg.get("type")
    if msg_type == "response":
        success = msg.get("success")
        err = msg.get("message", "")
        body_str = json.dumps(msg.get("body", {}))[:200]
        print(f"<<< RESP seq={msg.get('request_seq')} cmd={msg.get('command')} ok={success} err='{err}' body={body_str}")
    elif msg_type == "event":
        print(f"<<< EVENT {msg.get('event')} body={json.dumps(msg.get('body', {}))[:200]}")
    return msg


async def read_all(stdout, timeout=3.0):
    messages = []
    while True:
        try:
            msg = await read_dap(stdout, timeout=timeout)
            if msg is None:
                print("<<< EOF")
                break
            messages.append(msg)
        except asyncio.TimeoutError:
            print(f"<<< (timeout {timeout}s)")
            break
    return messages


async def read_stderr_bg(proc):
    while True:
        line = await proc.stderr.readline()
        if not line:
            break
        print(f"STDERR: {line.decode('utf-8', errors='replace').rstrip()}")


async def main():
    output, dll = await dotnet_build("/tmp/debug-test/HelloDebug")
    if not dll:
        print(f"Build failed:\n{output}")
        return
    print(f"DLL: {dll}\n")

    proc = await asyncio.create_subprocess_exec(
        VSDBG_UI, "--interpreter=vscode", "--engineLogging",
        stdin=asyncio.subprocess.PIPE,
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.PIPE,
    )
    stderr_task = asyncio.create_task(read_stderr_bg(proc))

    # 1. Initialize
    await write_dap(proc.stdin, 1, "initialize", {
        "clientID": "debugger-net-mcp",
        "adapterID": "coreclr",
        "pathFormat": "path",
        "linesStartAt1": True,
        "columnsStartAt1": True,
        "supportsRunInTerminalRequest": False,
    })
    await read_all(proc.stdout, timeout=5.0)

    # 2. Launch
    await write_dap(proc.stdin, 2, "launch", {
        "program": dll,
        "cwd": str(Path(dll).parent),
        "stopAtEntry": True,
        "type": "coreclr",
        "request": "launch",
    })
    await read_all(proc.stdout, timeout=15.0)

    # 3. Set breakpoints
    await write_dap(proc.stdin, 3, "setBreakpoints", {
        "source": {"path": "/tmp/debug-test/HelloDebug/Program.cs"},
        "breakpoints": [{"line": 6}],
    })
    await read_all(proc.stdout, timeout=5.0)

    # 4. ConfigurationDone
    await write_dap(proc.stdin, 4, "configurationDone")
    await read_all(proc.stdout, timeout=15.0)

    # 5. If stopped, inspect
    print("\n--- Trying threads + stacktrace ---")
    await write_dap(proc.stdin, 5, "threads")
    threads_msgs = await read_all(proc.stdout, timeout=5.0)

    await write_dap(proc.stdin, 6, "stackTrace", {
        "threadId": 1, "startFrame": 0, "levels": 5,
    })
    st_msgs = await read_all(proc.stdout, timeout=5.0)

    # 6. Get scopes and variables
    print("\n--- Trying scopes + variables ---")
    for m in st_msgs:
        if m.get("type") == "response" and m.get("success"):
            frames = m.get("body", {}).get("stackFrames", [])
            if frames:
                fid = frames[0]["id"]
                await write_dap(proc.stdin, 7, "scopes", {"frameId": fid})
                scope_msgs = await read_all(proc.stdout, timeout=5.0)
                for sm in scope_msgs:
                    if sm.get("type") == "response" and sm.get("success"):
                        for scope in sm.get("body", {}).get("scopes", []):
                            await write_dap(proc.stdin, 8, "variables", {
                                "variablesReference": scope["variablesReference"],
                            })
                            await read_all(proc.stdout, timeout=5.0)

    # 7. Continue
    print("\n--- Continue ---")
    await write_dap(proc.stdin, 9, "continue", {"threadId": 1})
    await read_all(proc.stdout, timeout=10.0)

    # 8. Disconnect
    print("\n--- Disconnecting ---")
    await write_dap(proc.stdin, 10, "disconnect", {"terminateDebuggee": True})
    await read_all(proc.stdout, timeout=5.0)

    if proc.returncode is None:
        proc.terminate()
        await proc.wait()
    stderr_task.cancel()
    print(f"\nDone! (exit={proc.returncode})")


asyncio.run(main())
