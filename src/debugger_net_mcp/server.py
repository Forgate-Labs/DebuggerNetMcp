"""MCP Server with 14 debug tools powered by FastMCP."""

import logging

from mcp.server.fastmcp import FastMCP

from debugger_net_mcp.session import get_session

logging.basicConfig(level=logging.INFO)

mcp = FastMCP(
    "debugger-net",
    instructions=(
        "Debug .NET Core applications interactively. "
        "Use debug_launch to start, set breakpoints, step through code, "
        "inspect variables, and evaluate C# expressions."
    ),
)


@mcp.tool()
async def debug_launch(
    project_path: str,
    stop_at_entry: bool = True,
    args: list[str] | None = None,
) -> dict:
    """Build and launch a .NET project with the debugger attached.

    Args:
        project_path: Path to the .csproj file or project directory.
        stop_at_entry: If True, pause at the first line of code (default: True).
        args: Optional command-line arguments to pass to the program.
    """
    session = get_session()
    return await session.launch(project_path, stop_at_entry=stop_at_entry, args=args)


@mcp.tool()
async def debug_attach(process_id: int) -> dict:
    """Attach the debugger to a running .NET process.

    Args:
        process_id: The PID of the .NET process to attach to.
    """
    session = get_session()
    return await session.attach(process_id)


@mcp.tool()
async def debug_set_breakpoint(
    file_path: str,
    line: int,
    condition: str | None = None,
) -> dict:
    """Set a breakpoint at a specific file and line.

    Can be called before or during a debug session. Breakpoints set before
    launching will be applied when the session starts.

    Args:
        file_path: Absolute path to the source file.
        line: Line number (1-based).
        condition: Optional C# condition expression (breakpoint only hits when true).
    """
    session = get_session()
    return await session.set_breakpoint(file_path, line, condition=condition)


@mcp.tool()
async def debug_remove_breakpoints(file_path: str) -> dict:
    """Remove all breakpoints from a file.

    Args:
        file_path: Absolute path to the source file.
    """
    session = get_session()
    return await session.remove_breakpoints(file_path)


@mcp.tool()
async def debug_continue(timeout: float = 30.0) -> dict:
    """Continue program execution until the next breakpoint or program end.

    This is a blocking call — it waits until the program stops (breakpoint,
    exception, or exit) or the timeout expires.

    Args:
        timeout: Maximum seconds to wait for a stop event (default: 30).
    """
    session = get_session()
    return await session.continue_execution(timeout=timeout)


@mcp.tool()
async def debug_step_over(timeout: float = 10.0) -> dict:
    """Step over to the next line in the current function.

    Executes the current line and stops at the next line in the same scope.
    If the current line has a function call, it executes the entire function.

    Args:
        timeout: Maximum seconds to wait for the step to complete.
    """
    session = get_session()
    return await session.step_over(timeout=timeout)


@mcp.tool()
async def debug_step_into(timeout: float = 10.0) -> dict:
    """Step into the function call on the current line.

    If the current line has a function call, enters that function and stops
    at its first line. Otherwise, behaves like step_over.

    Args:
        timeout: Maximum seconds to wait for the step to complete.
    """
    session = get_session()
    return await session.step_into(timeout=timeout)


@mcp.tool()
async def debug_step_out(timeout: float = 10.0) -> dict:
    """Step out of the current function.

    Continues execution until the current function returns, then stops
    at the calling line.

    Args:
        timeout: Maximum seconds to wait for the step to complete.
    """
    session = get_session()
    return await session.step_out(timeout=timeout)


@mcp.tool()
async def debug_stacktrace(levels: int = 20) -> dict:
    """Get the current call stack (requires program to be paused).

    Args:
        levels: Maximum number of stack frames to return (default: 20).
    """
    session = get_session()
    return await session.stacktrace(levels=levels)


@mcp.tool()
async def debug_variables(
    frame_index: int = 0,
    expand: str | None = None,
    scope: str = "Locals",
) -> dict:
    """Inspect variables in the current scope (requires program to be paused).

    Args:
        frame_index: Stack frame index (0 = current frame, 1 = caller, etc.).
        expand: Name of a variable to drill into (shows its properties/fields).
        scope: Scope to inspect: "Locals", "Arguments", etc. (default: "Locals").
    """
    session = get_session()
    return await session.variables(frame_index=frame_index, expand=expand, scope=scope)


@mcp.tool()
async def debug_evaluate(expression: str, frame_index: int = 0) -> dict:
    """Evaluate a C# expression in the current debug context (requires program to be paused).

    Args:
        expression: C# expression to evaluate (e.g., "myVar.Count", "x + y", "DateTime.Now").
        frame_index: Stack frame index for the evaluation context (default: 0 = current).
    """
    session = get_session()
    return await session.evaluate(expression, frame_index=frame_index)


@mcp.tool()
async def debug_pause() -> dict:
    """Pause program execution (when the program is running)."""
    session = get_session()
    return await session.pause()


@mcp.tool()
async def debug_disconnect() -> dict:
    """Disconnect and terminate the debug session.

    Kills the debuggee process and cleans up resources. Breakpoints are preserved
    for the next session.
    """
    session = get_session()
    return await session.disconnect()


@mcp.tool()
async def debug_status() -> dict:
    """Get the current debug session status.

    Returns state, current location (if stopped), recent program output,
    and registered breakpoints.
    """
    session = get_session()
    return session.status()


def main():
    mcp.run(transport="stdio")
