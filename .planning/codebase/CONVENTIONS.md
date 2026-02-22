# Coding Conventions

**Analysis Date:** 2026-02-22

## Naming Patterns

**Files:**
- Lowercase with underscores: `dap_client.py`, `dotnet_utils.py`, `session.py`, `server.py`
- Test files use `test_` prefix: `test_integration.py`, `test_dap_minimal.py`, `test_dap_protocol.py`, `test_dap_raw.py`
- Module init file: `__init__.py` (empty or minimal)
- Entry point: `__main__.py`

**Functions:**
- Lowercase with underscores for regular functions: `find_netcoredbg()`, `dotnet_build()`
- Prefix with underscore for internal/private functions: `_needs_strace_workaround()`, `_find_dll_in_output()`, `_require_state()`
- Async functions use `async def`: `async def send_request()`, `async def launch()`
- Tool functions (MCP server) use prefix: `debug_launch`, `debug_continue`, `debug_step_over`

**Variables:**
- Lowercase with underscores: `self._process`, `self._seq`, `self._pending`, `self._event_queues`
- Type-hinted attributes in classes: `self._process: asyncio.subprocess.Process | None`
- Loop variables use single/short names: `for f in frames:`, `for v in variables:`, `for bp in bps:`
- Enums use UPPERCASE with underscore: `SessionState.IDLE`, `SessionState.RUNNING`, `SessionState.STOPPED`

**Types:**
- Uses Python 3.11+ union syntax: `str | None`, `dict | None`, `list[dict]`
- Type hints on all function signatures (required)
- Return type annotations: `-> bool`, `-> dict`, `-> tuple[str, str | None]`
- Uses `defaultdict(asyncio.Queue)` for lazy queue initialization

## Code Style

**Formatting:**
- No explicit formatter configuration detected (no `.prettierrc`, `pyproject.toml` [tool.black])
- Implicit style: 4-space indentation (standard Python)
- Long lines present but not enforced to be broken (see `dap_client.py` line 119 ~85 chars)
- Uses `f-strings` for string formatting: `f"{error_msg}: {detail}"`

**Linting:**
- No linter configuration found (no `.eslintrc`, `pylintrc`, `flake8`)
- Style appears to follow implicit PEP 8 with some pragmatism (long lines not strictly limited)

## Import Organization

**Order:**
1. Standard library imports (alphabetical): `asyncio`, `json`, `logging`, `os`, `platform`, `re`, `shutil`, `sys`
2. Collection/special imports: `from collections import defaultdict, deque` and `from enum import Enum`
3. Third-party imports: `from mcp.server.fastmcp import FastMCP`
4. Local package imports: `from debugger_net_mcp.dap_client import DapClient`

**Path Aliases:**
- No path aliases configured. Uses relative imports from package root: `from debugger_net_mcp.session import get_session`

**Example from `session.py`:**
```python
import asyncio
import logging
from collections import deque
from enum import Enum
from pathlib import Path

from debugger_net_mcp.dap_client import DapClient
from debugger_net_mcp.dotnet_utils import dotnet_build
```

## Error Handling

**Patterns:**
- Explicit exception catching: `except asyncio.CancelledError:`, `except asyncio.TimeoutError:`, `except Exception:`
- Re-raise for control flow: `except asyncio.CancelledError: raise` (propagate cancellation)
- Broad `except Exception:` used in background tasks to log and continue: `except Exception: logger.exception("DAP reader loop error")`
- Failure returns in high-level functions: `return {"success": False, "error": "Build failed"}` instead of raising
- State validation before operations: `self._require_state(SessionState.STOPPED)` raises `RuntimeError`

**Example from `session.py` (graceful shutdown):**
```python
async def stop(self) -> None:
    # ...
    if self._process and self._process.returncode is None:
        self._process.terminate()
        try:
            await asyncio.wait_for(self._process.wait(), timeout=5)
        except asyncio.TimeoutError:
            self._process.kill()
            await self._process.wait()
```

## Logging

**Framework:** Standard library `logging` module

**Patterns:**
- Module-level logger: `logger = logging.getLogger(__name__)`
- Configured at server entry point: `logging.basicConfig(level=logging.INFO)` in `server.py`
- Warning/info for user-visible issues: `logger.warning("Timed out waiting for 'initialized' event")`
- Exception logging in background tasks: `logger.exception("DAP reader loop error")`
- Info for informational events: `logger.info("Using strace workaround for kernel %s", platform.release())`

**Example from `dap_client.py`:**
```python
logger = logging.getLogger(__name__)
# ...
logger.warning("Kernel %s may need strace workaround...")
logger.info("Using strace workaround for kernel %s", platform.release())
```

## Comments

**When to Comment:**
- Top-of-file module docstrings explain purpose: `"""Async DAP (Debug Adapter Protocol) client..."""`
- Function docstrings required for public API: `"""Build and launch a .NET project with the debugger attached."""`
- Complex protocol sequences documented inline: `# Initialize → launch(stopAtEntry=true) → wait "initialized"...`
- Workarounds and gotchas explained: `# Workaround for kernel race condition causing SIGSEGV in netcoredbg`
- Inline comments on tricky logic: `# Empty line = end of headers`

**Docstring Style:** Google/NumPy style docstrings for public functions:
```python
"""Get variables in the current scope. Use expand to drill into an object.

Args:
    frame_index: Stack frame index (0 = current frame, 1 = caller, etc.).
    expand: Name of a variable to drill into (shows its properties/fields).
    scope: Scope to inspect: "Locals", "Arguments", etc. (default: "Locals").
"""
```

## Function Design

**Size:** Functions are well-scoped, typically 5-50 lines for specific tasks. State machines use longer methods (~40-80 lines) but break into helper methods.

**Parameters:**
- Maximum 3-4 positional parameters (see `set_breakpoint(file_path, line, condition=None)`)
- Optional parameters use defaults: `timeout: float = 30.0`
- No positional-only parameters used

**Return Values:**
- Async functions return `dict` for tool responses: `-> dict`
- Helper functions return specific types: `-> str | None`, `-> bool`
- Functions prefer returning data over raising exceptions in public API
- Example: `dotnet_build()` returns `tuple[str, str | None]` (output_text, dll_path_or_None)

## Module Design

**Exports:**
- `server.py` exports tool functions and `main()` entry point
- `session.py` exports `DebugSession` class and `get_session()` singleton getter
- `dap_client.py` exports `DapClient` class and utility functions `find_netcoredbg()`, `_needs_strace_workaround()`
- `dotnet_utils.py` exports `dotnet_build()` (public) and helper functions (private with `_` prefix)

**Barrel Files:** Not used. Imports are explicit from specific modules.

**Singleton Pattern:**
- Used for debug session: `get_session()` returns global singleton `_session`
- Allows MCP tools to share state across calls

## Special Conventions

**Async/Await:**
- All network/subprocess I/O is async: uses `asyncio.subprocess.Process`, `asyncio.Queue`, `asyncio.Event`
- Background tasks created with `asyncio.create_task()`: `self._output_listener = asyncio.create_task(self._listen_outputs())`
- Cleanup of tasks with cancellation: `task.cancel(); await task` wrapped in try/except `asyncio.CancelledError`

**State Machines:**
- Use `Enum` for states: `class SessionState(Enum): IDLE, INITIALIZING, RUNNING, STOPPED, TERMINATED`
- State validation: `_require_state()` method prevents invalid transitions
- Track state in private attribute: `self._state`

**DAP Protocol Handling:**
- Message framing: `Content-Length: N\r\n\r\n{body}`
- Request/response correlation via sequence numbers: `self._seq`, `self._pending[seq]`
- Event queue per event type: `self._event_queues[event_name]`
- Background reader loop: `asyncio.create_task(self._reader_loop())`

---

*Convention analysis: 2026-02-22*
