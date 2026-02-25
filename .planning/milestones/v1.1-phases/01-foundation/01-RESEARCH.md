# Phase 1: Foundation - Research

**Researched:** 2026-02-22
**Domain:** .NET 10 Solution Setup, CMake/C Shared Library, ptrace PTRACE_SEIZE, Claude Code MCP Registration
**Confidence:** HIGH

## Summary

Phase 1 is a pure scaffolding and infrastructure phase. No debugging logic is implemented here. The work falls into three distinct areas: (1) wiping all Python artifacts from the repo, (2) creating a clean four-project .NET 10 solution with correct structure, and (3) creating a standalone CMake project that compiles a C shared library (`libdotnetdbg.so`) exposing five ptrace-based symbols. Build and install scripts tie everything together.

The Native project (C/CMake) is NOT part of the .NET solution file. The `.sln` file holds only the three managed C# projects (Core, Mcp, Tests). `build.sh` orchestrates both build systems sequentially: CMake first, then `dotnet build`. This design is mandated by INFRA-04 and is consistent with the requirement that `dotnet build` must work independently after CMake produces the shared library.

The ptrace wrapper is straightforward on this system: GCC 13, CMake 4.2, and Linux headers all support `PTRACE_SEIZE` and `PTRACE_INTERRUPT`. The `__attribute__((visibility("default")))` + `-fvisibility=hidden` pattern for controlled symbol export compiles and works correctly on this machine (verified). `nm -D` confirms exported symbols after build.

**Primary recommendation:** Follow the exact four-step sequence — delete Python artifacts, scaffold .NET solution, write CMake C library, write build.sh/install.sh — in that order. No library dependencies are required for Phase 1 beyond what ships with the SDK.

## Standard Stack

### Core

| Library / Tool | Version | Purpose | Why Standard |
|----------------|---------|---------|--------------|
| .NET SDK | 10.0.100 (installed) | Target framework for all managed projects | Specified in INFRA-02/INFRA-03 |
| CMake | 4.2.1 (installed) | Build system for native C shared library | Specified in NATIVE-02 |
| GCC | 13.3.0 (installed) | C compiler for ptrace_wrapper.c | System default, verified |
| xUnit | latest (via template) | Test framework for DebuggerNetMcp.Tests | Specified in INFRA-02 |

### Supporting

| Tool | Version | Purpose | When to Use |
|------|---------|---------|-------------|
| `nm -D` | system | Verify exported symbols in .so | After cmake --build to confirm dbg_* symbols |
| `dotnet sln add` | 10.0.100 | Add .csproj projects to solution | During scaffold, after dotnet new |
| `claude mcp add` | installed at ~/.local/bin/claude | Register MCP server with Claude Code | In install.sh |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `dotnet new sln` (`.sln` format) | `.slnx` (new XML format) | `.sln` is more universal; `.slnx` is newer but less tooling support |
| `fvisibility=hidden` + explicit exports | Export all symbols (no flag) | Hidden default + explicit attributes is the standard pattern; avoids symbol leaks |
| `dotnet run --project` in install.sh | Published binary path | `dotnet run` is simpler for dev registration; publish is better for production |

**Installation:**
No additional NuGet packages are needed for Phase 1. NuGet packages (ModelContextProtocol, etc.) are Phase 4 concerns.

## Architecture Patterns

### Recommended Project Structure

```
DebuggerNetMcp/                    ← repo root
├── global.json                    ← SDK pin: 10.0.0, rollForward latestMinor
├── DebuggerNetMcp.sln             ← holds Core + Mcp + Tests only
├── build.sh                       ← CMake build then dotnet build
├── install.sh                     ← claude mcp add registration
├── native/                        ← CMake project (NOT in .sln)
│   ├── CMakeLists.txt
│   └── src/
│       └── ptrace_wrapper.c
├── src/
│   ├── DebuggerNetMcp.Core/       ← class library
│   │   └── DebuggerNetMcp.Core.csproj
│   └── DebuggerNetMcp.Mcp/        ← console app (MCP server entry point)
│       └── DebuggerNetMcp.Mcp.csproj
└── tests/
    └── DebuggerNetMcp.Tests/      ← xUnit test project
        └── DebuggerNetMcp.Tests.csproj
```

**Note on Native placement:** The `native/` directory lives at the repo root, parallel to `src/`. CMake outputs `libdotnetdbg.so` into `native/build/` (or a designated output dir like `lib/`). `build.sh` copies the `.so` to a known location (e.g., `lib/`) that `src/DebuggerNetMcp.Core/` will reference in later phases.

### Pattern 1: .NET Solution Scaffold

**What:** Create solution + three projects + wire references
**When to use:** Standard multi-project .NET setup
**Sequence:**
```bash
# 1. Pin SDK
cat > global.json << 'EOF'
{
  "sdk": {
    "version": "10.0.0",
    "rollForward": "latestMinor"
  }
}
EOF

# 2. Create solution
dotnet new sln -n DebuggerNetMcp

# 3. Create projects (all default to net10.0 with SDK 10.0.100)
dotnet new classlib -n DebuggerNetMcp.Core -o src/DebuggerNetMcp.Core
dotnet new console  -n DebuggerNetMcp.Mcp  -o src/DebuggerNetMcp.Mcp
dotnet new xunit    -n DebuggerNetMcp.Tests -o tests/DebuggerNetMcp.Tests

# 4. Add to solution
dotnet sln add src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj
dotnet sln add src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj
dotnet sln add tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj

# 5. Wire project references (Mcp depends on Core; Tests depends on Core)
dotnet add src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj reference src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj
dotnet add tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj reference src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj

# 6. Build to verify
dotnet build
```

**Verified:** All templates default to `net10.0` when using SDK 10.0.100. No `-f net10.0` flag required.

### Pattern 2: CMake Shared Library with Controlled Exports

**What:** Compile C code into `.so` with only the required symbols exported
**When to use:** NATIVE-02 — produce `libdotnetdbg.so`
**Example CMakeLists.txt:**
```cmake
cmake_minimum_required(VERSION 3.20)
project(dotnetdbg C)

add_library(dotnetdbg SHARED src/ptrace_wrapper.c)

set_target_properties(dotnetdbg PROPERTIES
    OUTPUT_NAME "dotnetdbg"
    C_VISIBILITY_PRESET hidden
    POSITION_INDEPENDENT_CODE ON
)

target_compile_options(dotnetdbg PRIVATE -fvisibility=hidden)
```

**Example ptrace_wrapper.c skeleton:**
```c
#include <sys/ptrace.h>
#include <sys/types.h>
#include <sys/wait.h>
#include <errno.h>

#define EXPORT __attribute__((visibility("default")))

EXPORT int dbg_attach(pid_t pid) {
    if (ptrace(PTRACE_SEIZE, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

EXPORT int dbg_detach(pid_t pid) {
    if (ptrace(PTRACE_DETACH, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

EXPORT int dbg_interrupt(pid_t pid) {
    // PTRACE_INTERRUPT only valid after PTRACE_SEIZE
    if (ptrace(PTRACE_INTERRUPT, pid, NULL, NULL) == -1)
        return -errno;
    return 0;
}

EXPORT int dbg_continue(pid_t pid, int sig) {
    if (ptrace(PTRACE_CONT, pid, NULL, (void*)(long)sig) == -1)
        return -errno;
    return 0;
}

EXPORT int dbg_wait(pid_t pid, int *status, int flags) {
    pid_t result = waitpid(pid, status, flags);
    if (result == -1)
        return -errno;
    return (int)result;
}
```

**Verification (after cmake --build):**
```bash
nm -D native/build/libdotnetdbg.so | grep "T dbg_"
# Expected: dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait
```

**Locally verified:** `gcc -shared -fPIC -fvisibility=hidden` with `__attribute__((visibility("default")))` exports only tagged symbols. `nm -D` confirms. PTRACE_SEIZE (0x4206), PTRACE_INTERRUPT (0x4207), PTRACE_CONT (7), PTRACE_DETACH (17) all compile correctly on kernel 6.17.

### Pattern 3: build.sh Orchestration

**What:** Single script to build native then managed
**When to use:** INFRA-04
```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "==> Building native library (CMake)..."
cmake -S "$SCRIPT_DIR/native" -B "$SCRIPT_DIR/native/build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$SCRIPT_DIR/native/build" --parallel

# Copy .so to a location accessible by managed code (for later phases)
mkdir -p "$SCRIPT_DIR/lib"
cp "$SCRIPT_DIR/native/build/libdotnetdbg.so" "$SCRIPT_DIR/lib/"

echo "==> Building managed projects (dotnet)..."
dotnet build "$SCRIPT_DIR" -c Release

echo "==> Build complete."
```

### Pattern 4: install.sh MCP Registration

**What:** Register the MCP server with Claude Code at user scope
**When to use:** INFRA-05
```bash
#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
CLAUDE_BIN="${CLAUDE_BIN:-claude}"

# Remove existing registration (idempotent)
"$CLAUDE_BIN" mcp remove debugger-net -s user 2>/dev/null || true

# Register with user scope, stdio transport
"$CLAUDE_BIN" mcp add \
    -s user \
    -e DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}" \
    -e LD_LIBRARY_PATH="$SCRIPT_DIR/lib:${LD_LIBRARY_PATH:-}" \
    debugger-net \
    dotnet -- run --project "$SCRIPT_DIR/src/DebuggerNetMcp.Mcp" --no-build -c Release

echo "==> MCP server 'debugger-net' registered at user scope."
```

**Key points about `claude mcp add`:**
- `--scope user` (`-s user`) stores in user-level settings (not local/project scope)
- `-e KEY=VALUE` passes environment variables to the spawned process
- `-- dotnet run --project ...` is the command after the `--` separator
- The `--no-build` flag prevents dotnet from rebuilding on each MCP invocation (build.sh handles that separately)
- `claude` binary is at `~/.local/bin/claude` on this system

### Anti-Patterns to Avoid

- **Adding the Native/CMake directory to the .sln:** `dotnet sln add` only accepts `.csproj` files. The CMake project is completely separate and must NOT be added.
- **Using PTRACE_ATTACH instead of PTRACE_SEIZE:** PTRACE_ATTACH sends SIGSTOP which behaves differently on kernel 6.12+. PTRACE_SEIZE is the correct choice per the requirements.
- **Hardcoding paths in install.sh:** Use `$SCRIPT_DIR` computed at runtime. Hardcoded paths break when the repo is moved.
- **Running `dotnet build` inside install.sh:** install.sh only registers; build.sh builds. These concerns are separate per INFRA-04 vs INFRA-05.
- **Using `dotnet new` without `-o` flag:** Without `-o`, projects are created in the current directory with confusing layouts.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Shared library symbol export control | Manual `.map` file or export table | `__attribute__((visibility("default")))` + `-fvisibility=hidden` | Standard GCC pattern, verified on this system |
| Solution-level build | Custom Makefile wrapping dotnet | `dotnet build <sln>` | The .sln file drives multi-project builds natively |
| SDK version pinning | Environment variable hacks | `global.json` with `rollForward: latestMinor` | This is the official .NET mechanism |
| MCP server registration | Manual JSON editing | `claude mcp add` CLI | The CLI writes the correct settings file format |

**Key insight:** Phase 1 uses only standard tooling. The complexity is in sequencing, not in the implementation.

## Common Pitfalls

### Pitfall 1: Python Artifact Removal Order
**What goes wrong:** Removing `src/` while `uv.lock`/`pyproject.toml` still reference it causes `uv` to error if anything tries to run.
**Why it happens:** `uv` may be invoked by pre-commit hooks or shell aliases.
**How to avoid:** Delete `src/`, `pyproject.toml`, `uv.lock`, `requirements*.txt`, `test_*.py` files, and the registered MCP entry (if Python version is registered) in a single commit. Verify with `git status` before committing.
**Warning signs:** Any remaining `.py` file not deleted counts as a failure per success criterion 1.

### Pitfall 2: PTRACE_INTERRUPT vs PTRACE_ATTACH semantics
**What goes wrong:** Using `PTRACE_ATTACH` in dbg_attach instead of `PTRACE_SEIZE`.
**Why it happens:** PTRACE_ATTACH is more commonly documented; PTRACE_SEIZE is the newer kernel 6.12+ safe approach.
**How to avoid:** PTRACE_SEIZE is at `0x4206` in the linux ptrace header. PTRACE_INTERRUPT (`0x4207`) is only valid after PTRACE_SEIZE — it replaces the old SIGSTOP approach.
**Warning signs:** If dbg_interrupt tries to use PTRACE_INTERRUPT on a process attached with PTRACE_ATTACH, it will fail with EINVAL.

### Pitfall 3: dotnet build Succeeds but .sln is Missing a Project
**What goes wrong:** `dotnet build` in a project directory succeeds, but `dotnet build DebuggerNetMcp.sln` fails because a project wasn't added via `dotnet sln add`.
**Why it happens:** Developers test individual projects during creation and miss the `sln add` step.
**How to avoid:** Always run `dotnet sln list` after scaffolding to verify all three projects appear.
**Warning signs:** `dotnet sln list` shows fewer than 3 projects.

### Pitfall 4: cmake --build Output Path Mismatch
**What goes wrong:** `libdotnetdbg.so` is built but in a different path than `build.sh` expects when copying to `lib/`.
**Why it happens:** CMake's `CMAKE_LIBRARY_OUTPUT_DIRECTORY` defaults to the build directory root, but configuration-specific subdirs (`Release/`, `Debug/`) may be added on some platforms.
**How to avoid:** Explicitly set `CMAKE_LIBRARY_OUTPUT_DIRECTORY` in CMakeLists.txt, or use `find native/build -name "libdotnetdbg.so"` in build.sh to locate it dynamically.

### Pitfall 5: install.sh Fails if claude Binary Not in PATH
**What goes wrong:** `install.sh` uses bare `claude` which isn't in the default PATH.
**Why it happens:** `claude` is installed at `~/.local/bin/claude` (verified on this system), which may not be in PATH in all shell contexts.
**How to avoid:** The install.sh pattern above uses `CLAUDE_BIN` env var with fallback: `"${CLAUDE_BIN:-claude}"`. The user can set `CLAUDE_BIN=~/.local/bin/claude ./install.sh` if needed.

### Pitfall 6: .NET Project References Not Wired
**What goes wrong:** `dotnet build` succeeds on individual projects but fails at solution level because Core types aren't found in Mcp/Tests.
**Why it happens:** `dotnet new` doesn't add project references automatically.
**How to avoid:** Explicitly run `dotnet add reference` for Mcp→Core and Tests→Core after creating projects.

## Code Examples

Verified patterns from system inspection and documentation:

### Verify Exported Symbols After CMake Build
```bash
# Source: verified locally with nm on this system
nm -D native/build/libdotnetdbg.so | grep " T dbg_"
# Expected output (5 lines):
#   0000... T dbg_attach
#   0000... T dbg_continue
#   0000... T dbg_detach
#   0000... T dbg_interrupt
#   0000... T dbg_wait
```

### global.json Format
```json
{
  "sdk": {
    "version": "10.0.0",
    "rollForward": "latestMinor"
  }
}
```
Source: `dotnet new globaljson` template, verified format via `python3 -m json.tool`.

### CMake Build Invocation
```bash
# Out-of-source build (correct)
cmake -S native/ -B native/build -DCMAKE_BUILD_TYPE=Release
cmake --build native/build --parallel
```

### Verify Solution Contains All Projects
```bash
dotnet sln list
# Should show:
# src/DebuggerNetMcp.Core/DebuggerNetMcp.Core.csproj
# src/DebuggerNetMcp.Mcp/DebuggerNetMcp.Mcp.csproj
# tests/DebuggerNetMcp.Tests/DebuggerNetMcp.Tests.csproj
```

### Delete Python Artifacts
```bash
git rm -r src/ pyproject.toml uv.lock
# Also remove any test_*.py files at root
git rm test_dap_minimal.py test_dap_protocol.py test_dap_raw.py test_integration.py
```
Note: `src/` currently contains `debugger_net_mcp/` Python package. After deletion, `src/` will be recreated by `dotnet new` for C# projects.

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| PTRACE_ATTACH | PTRACE_SEIZE | Linux 3.4+ | SEIZE doesn't send SIGSTOP; safer on 6.12+ kernels |
| `.sln` XML format | `.slnx` XML format | .NET 9+ | Both supported; `.sln` is still the default |
| `dotnet new` without `--output` | `dotnet new -o path/` | Early .NET | Explicit output path prevents accidental cwd pollution |

**Deprecated/outdated:**
- `PTRACE_ATTACH` for new code: still functional but causes race on kernel 6.12+ with ICorDebug. Replaced by PTRACE_SEIZE.
- Python/uv infrastructure: eliminated entirely in this rewrite.

## Open Questions

1. **build.sh: should CMake output go directly to `lib/` or via `native/build/`?**
   - What we know: `cmake --build` outputs to the build dir; a copy step is needed
   - What's unclear: Whether to use `CMAKE_LIBRARY_OUTPUT_DIRECTORY` to write directly to `lib/` vs copy in build.sh
   - Recommendation: Set `CMAKE_LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../lib"` in CMakeLists.txt for Phase 1 simplicity; avoids a copy step in build.sh

2. **install.sh: dotnet run vs published binary**
   - What we know: `dotnet run` is slow (re-compiles check); published binary is fast but requires publish step
   - What's unclear: Whether Phase 1 install.sh should use `dotnet run` (simpler, Phase 1 appropriate) or establish the published binary pattern (better for production)
   - Recommendation: Use `dotnet run --no-build` for Phase 1 (Phase 4 adds the publish step if needed)

3. **DebuggerNetMcp.Native: top-level `native/` vs `src/DebuggerNetMcp.Native/`**
   - What we know: Requirements say "4 projects" including Native, but it can't be in the .sln
   - What's unclear: Whether `native/` should be at root or under `src/` for consistency
   - Recommendation: Place at root as `native/` — it's a fundamentally different build system, co-locating under `src/` with C# projects would be confusing

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| INFRA-01 | Delete all Python code (src/, pyproject.toml, uv.lock, requirements*.txt) | Files to delete are confirmed: `src/debugger_net_mcp/`, `pyproject.toml`, `uv.lock`, `test_*.py` at root. Use `git rm -r` for tracked files. |
| INFRA-02 | Create .NET 10 solution with 4 projects: Native (C/CMake), Core (classlib), Mcp (console), Tests (xUnit) | `dotnet new sln/classlib/console/xunit` all default to net10.0 with SDK 10.0.100. Native is CMake-only (not in .sln). Use `dotnet sln add` for the 3 managed projects. |
| INFRA-03 | global.json with SDK 10.0.0, rollForward latestMinor | Format verified: `{"sdk": {"version": "10.0.0", "rollForward": "latestMinor"}}`. SDK 10.0.100 is installed and satisfies `latestMinor` rollForward. |
| INFRA-04 | build.sh compiles Native with CMake, copies libdotnetdbg.so, runs dotnet build -c Release | Pattern: `cmake -S native/ -B native/build -DCMAKE_BUILD_TYPE=Release && cmake --build native/build && cp ... && dotnet build -c Release`. |
| INFRA-05 | install.sh registers MCP in Claude Code via `claude mcp add` | `claude` is at `~/.local/bin/claude`. Command: `claude mcp add -s user -e KEY=VAL debugger-net dotnet -- run --project src/DebuggerNetMcp.Mcp --no-build -c Release`. |
| NATIVE-01 | ptrace_wrapper.c with PTRACE_SEIZE — functions: dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait | All constants verified present: PTRACE_SEIZE (0x4206), PTRACE_INTERRUPT (0x4207), PTRACE_CONT (7), PTRACE_DETACH (17). `waitpid` for dbg_wait. Compiles correctly on GCC 13/kernel 6.17. |
| NATIVE-02 | CMakeLists.txt compiling as libdotnetdbg.so | `add_library(dotnetdbg SHARED ...)` + `C_VISIBILITY_PRESET hidden` + `EXPORT` macro. Verified: `nm -D` shows exported symbols. Symbol names: dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait. |
</phase_requirements>

## Sources

### Primary (HIGH confidence)
- System inspection: `nm`, `gcc`, `cmake --version`, ptrace headers at `/usr/include/x86_64-linux-gnu/sys/ptrace.h` and `/usr/include/linux/ptrace.h`
- Verified compile test: `gcc -shared -fPIC -fvisibility=hidden` with visibility attributes — confirmed working
- Verified ptrace constants compile: PTRACE_SEIZE, PTRACE_INTERRUPT, PTRACE_CONT, PTRACE_DETACH — all confirmed in headers
- `dotnet --list-sdks` — SDK 10.0.100 confirmed installed
- `dotnet new classlib/console/xunit --help` — net10.0 is default with SDK 10.0.100
- `claude mcp add --help` — scope (-s user), env (-e), transport (stdio default), separator (--) syntax confirmed
- `man 2 ptrace` — PTRACE_SEIZE/PTRACE_INTERRUPT semantics confirmed
- `.planning/REQUIREMENTS.md` — authoritative source for requirement IDs and descriptions

### Secondary (MEDIUM confidence)
- README.md (existing) — confirms current Python registration approach and environment variables used
- `.planning/STATE.md` — architectural decisions: PTRACE_SEIZE, ICorDebug direct, no DAP

### Tertiary (LOW confidence)
- None — all critical claims verified through system tools or official file inspection

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all tools verified installed and functional on this exact system
- Architecture: HIGH — derived from REQUIREMENTS.md + direct tool verification
- Pitfalls: HIGH (for ptrace/cmake/dotnet pitfalls) — derived from system inspection; MEDIUM for operational pitfalls (ordering, paths) based on general .NET experience
- Phase requirements mapping: HIGH — all requirement IDs confirmed against REQUIREMENTS.md

**Research date:** 2026-02-22
**Valid until:** 2026-05-22 (stable tooling; CMake/dotnet/ptrace APIs don't change rapidly)
