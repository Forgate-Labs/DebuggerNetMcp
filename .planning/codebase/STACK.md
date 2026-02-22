# Technology Stack

**Analysis Date:** 2026-02-22

## Languages

**Primary:**
- Python 3.11+ - Server logic, DAP client, session management
- C# - Target debug projects (.NET 8.0+)

**Configuration/Markup:**
- TOML - pyproject.toml for package metadata

## Runtime

**Environment:**
- Python 3.11+ (required minimum)
- .NET SDK 8.0+ (for building target projects)
- netcoredbg 3.1.3 - Debug adapter binary (separate from this project)

**Package Manager:**
- uv - Python package manager (recommended)
- pip - Alternative package manager
- Lockfile: uv.lock (present, version pinned)

## Frameworks

**Core Framework:**
- mcp[cli] 1.26.0 - Model Context Protocol server framework
  - Purpose: Stdio-based protocol server for exposing debug tools to Claude Code

**Web/Async:**
- starlette 0.41.3 - ASGI web framework (dependency via mcp)
- uvicorn 0.40.0 - ASGI server (dependency via mcp)
- httpx 0.28.1 - Async HTTP client library (dependency via mcp)
- anyio 4.12.1 - Async abstraction layer (dependency via mcp)

**CLI/Development:**
- typer 0.15.1 - CLI framework via MCP
- click 8.3.1 - CLI utility (dependency via starlette)
- rich 13.10.0 - Terminal formatting (optional enhancement)

**Data/Validation:**
- pydantic 2.10.5 - Data validation and settings
- pydantic-settings 2.7.1 - Environment-based configuration
- pydantic-core 2.27.2 - Pydantic core library

**Async Processing:**
- sse-starlette 3.2.0 - Server-sent events (via mcp)
- httpx-sse 0.4.3 - HTTP streaming support

## Key Dependencies

**Critical:**
- mcp 1.26.0 - Enables MCP protocol, enables `FastMCP` decorator-based tool registration in `server.py`
- python-dotenv 1.2.1 - Loads .env files for environment configuration

**Infrastructure:**
- cryptography 44.0.0 - Security primitives (dependency via various packages)
- pycparser 2.22 - C parser (dependency via cffi)
- cffi 2.0.0 - C Foreign Function Interface for native bindings

**Security/Certificates:**
- certifi 2026.1.4 - CA certificate bundle for HTTPS validation
- PyJWT 2.11.0 - JWT token handling (may be used by mcp)

**Utilities:**
- attrs 25.4.0 - Class decorators (dependency via jsonschema)
- annotated-types 0.7.0 - Type annotation support (dependency via pydantic)
- idna 3.17 - Internationalized domain names (dependency via anyio)
- markdown-it-py 4.0.0 - Markdown parser (via rich)
- shellingham 1.5.4 - Shell detection (via typer)
- typing-extensions 4.12.2 - Type system backports
- typing-inspection 0.9.0 - Type introspection

**Schemas/Validation:**
- jsonschema 4.23.0 - JSON schema validation
- jsonschema-specifications 2025.1.1 - JSON schema spec (dependency)
- rpds-py 0.21.1 - Data structures (dependency via jsonschema)
- referencing 0.35.1 - JSON schema references (dependency)

**Platform-specific:**
- pywin32 308 - Windows API access (Windows only, optional)

## Configuration

**Environment:**
- Reads .env files via python-dotenv (if present)
- Environment variables required (set by Claude mcp registration):
  - `DOTNET_ROOT` - Path to .NET runtime root
  - `NETCOREDBG_PATH` - Path to netcoredbg binary (optional, auto-detected)
  - `LD_LIBRARY_PATH` - Library search path for libdbgshim.so

**Optional Environment:**
- `DEBUGGER_NET_MCP_NO_STRACE=1` - Disables strace workaround for kernel race condition

**Build:**
- pyproject.toml - Project metadata, dependencies, build backend (hatchling)
- uv.lock - Reproducible dependency lock file with pinned versions

## Platform Requirements

**Development:**
- Python 3.11+ installed
- uv or pip for dependency management
- .NET SDK 8.0+ (for building debug target projects)
- netcoredbg binary (Samsung/netcoredbg release or source-built)
- strace (required on Linux kernel >= 6.12 for netcoredbg SIGSEGV workaround)

**Production/Deployment:**
- Python 3.11+ runtime environment
- netcoredbg installed and accessible
- .NET runtime matching target project versions
- Claude Code with MCP support (registration via `claude mcp add`)

**Platform Support:**
- Linux - Primary platform, kernel >= 6.12 needs strace workaround
- macOS - Supported (no strace needed)
- Windows - Supported (pywin32 available for Windows-specific APIs)

---

*Stack analysis: 2026-02-22*
