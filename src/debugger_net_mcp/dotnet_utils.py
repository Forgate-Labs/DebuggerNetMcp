"""Utilities for building .NET projects and discovering output DLLs."""

import asyncio
import re
from pathlib import Path


async def dotnet_build(project_path: str) -> tuple[str, str | None]:
    """Build a .NET project and return (build_output, dll_path).

    Returns:
        Tuple of (build_output_text, dll_path_or_None).
        If build fails, dll_path is None.
    """
    path = Path(project_path).resolve()

    if path.is_file():
        cwd = path.parent
        args = ["dotnet", "build", str(path), "-c", "Debug"]
    elif path.is_dir():
        cwd = path
        args = ["dotnet", "build", "-c", "Debug"]
    else:
        return f"Path not found: {project_path}", None

    proc = await asyncio.create_subprocess_exec(
        *args,
        cwd=str(cwd),
        stdout=asyncio.subprocess.PIPE,
        stderr=asyncio.subprocess.STDOUT,
    )
    stdout, _ = await proc.communicate()
    output = stdout.decode("utf-8", errors="replace")

    if proc.returncode != 0:
        return output, None

    dll_path = _find_dll_in_output(output)
    if not dll_path:
        dll_path = _find_dll_fallback(cwd)

    return output, dll_path


def _find_dll_in_output(output: str) -> str | None:
    """Extract DLL path from dotnet build output line like 'ProjectName -> /path/to.dll'."""
    match = re.search(r"->\s+(.+\.dll)\s*$", output, re.MULTILINE)
    if match:
        dll = match.group(1).strip()
        if Path(dll).exists():
            return dll
    return None


def _find_dll_fallback(project_dir: Path) -> str | None:
    """Search bin/Debug/ for a DLL matching the project name."""
    # Try to find project name from .csproj
    csprojs = list(project_dir.glob("*.csproj"))
    if not csprojs:
        return None

    project_name = csprojs[0].stem
    # Search bin/Debug/net*/ for the DLL
    for dll in project_dir.glob(f"bin/Debug/net*/{project_name}.dll"):
        return str(dll)

    return None
