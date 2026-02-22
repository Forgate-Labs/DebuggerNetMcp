---
phase: 01-foundation
plan: 03
subsystem: infra
tags: [bash, build, cmake, dotnet, mcp, install]

# Dependency graph
requires:
  - phase: 01-02
    provides: "native/CMakeLists.txt with LIBRARY_OUTPUT_DIRECTORY to lib/ (no copy step needed)"
  - phase: 01-01
    provides: "DebuggerNetMcp.sln with Core/Mcp/Tests projects for dotnet build"
provides:
  - "build.sh: single-command CMake + dotnet build orchestration at repo root"
  - "install.sh: idempotent MCP server registration via claude mcp add at user scope"
affects: [phase-2, phase-3, phase-4, phase-5, developer-experience]

# Tech tracking
tech-stack:
  added: [bash-build-script, claude-mcp-add]
  patterns:
    - "SCRIPT_DIR via BASH_SOURCE[0] — portable script location regardless of working directory"
    - "-- separator in claude mcp add to disambiguate -e variadic option from server name"
    - "Idempotent registration: mcp remove || true before mcp add"

key-files:
  created:
    - build.sh
    - install.sh
  modified: []

key-decisions:
  - "-- separator placed before server name in claude mcp add — -e is variadic and consumes positional args without it"
  - "--no-build flag in dotnet run — build.sh handles builds, MCP invocations skip rebuild overhead"
  - "CLAUDE_BIN overridable via env var — avoids hardcoding ~/.local/bin/claude in install.sh"

patterns-established:
  - "Pattern: build.sh as single-command dev experience wrapping both build systems (cmake + dotnet)"
  - "Pattern: install.sh removes then re-adds MCP registration for guaranteed idempotency"

requirements-completed: [INFRA-04, INFRA-05]

# Metrics
duration: 2min
completed: 2026-02-22
---

# Phase 01 Plan 03: Build and Install Scripts Summary

**build.sh and install.sh providing single-command CMake+dotnet build and idempotent MCP server registration via claude mcp add**

## Performance

- **Duration:** 2 min
- **Started:** 2026-02-22T21:44:00Z
- **Completed:** 2026-02-22T21:46:26Z
- **Tasks:** 2
- **Files modified:** 2 (both created)

## Accomplishments
- Created `build.sh` that runs cmake (native libdotnetdbg.so) then dotnet build (managed solution) in sequence
- Created `install.sh` that idempotently registers `debugger-net` MCP server at user scope with DOTNET_ROOT and LD_LIBRARY_PATH env vars
- `bash build.sh` exits 0 producing `lib/libdotnetdbg.so` and managed artifacts in `bin/Release/net10.0/`
- `bash install.sh` exits 0 and is safe to run multiple times (remove then re-add pattern)

## Task Commits

Each task was committed atomically:

1. **Task 1: Write build.sh (CMake + dotnet orchestration)** - `b9ab45a` (feat)
2. **Task 2: Write install.sh (MCP registration) and verify** - `8938d86` (feat)

**Plan metadata:** (docs commit follows)

## Files Created/Modified
- `/home/eduardo/Projects/DebuggerNetMcp/build.sh` - CMake + dotnet build orchestration, SCRIPT_DIR-relative, set -euo pipefail
- `/home/eduardo/Projects/DebuggerNetMcp/install.sh` - Idempotent MCP registration at user scope with env var forwarding

## Decisions Made
- `--` separator required before server name in `claude mcp add`: the `-e` flag is variadic (`<env...>`) so without `--`, the CLI consumes the server name `debugger-net` as an additional `-e` value and errors
- `--no-build` in dotnet run: prevents dotnet from rebuilding on each MCP tool invocation; `build.sh` is the designated build entry point
- `CLAUDE_BIN` is overridable via environment variable: avoids hardcoding `~/.local/bin/claude` and allows use in non-standard installations

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed -- separator position in claude mcp add command**
- **Found during:** Task 2 (install.sh verification)
- **Issue:** Plan put `--` between `dotnet` and `run` (`dotnet -- run ...`), but claude's `-e` flag is variadic and consumed the server name `debugger-net` as an env var value, causing "Invalid environment variable format: debugger-net" error
- **Fix:** Moved `--` before the server name (`... -e KEY=val -- debugger-net dotnet run ...`) per claude mcp add syntax where `--` separates options from positional args
- **Files modified:** install.sh
- **Verification:** `bash install.sh` exits 0, `debugger-net` appears in `~/.claude.json`
- **Committed in:** `8938d86` (Task 2 commit)

---

**1. [Rule 3 - Blocking] Fixed CRLF line endings in shell scripts**
- **Found during:** Task 1 (build.sh verification)
- **Issue:** Write tool produced files with Windows CRLF line endings (`\r\n`); bash `set -euo pipefail` with `\r` caused "invalid option name: pipefail" error
- **Fix:** Applied `tr -d '\r'` to strip carriage returns from both scripts before running
- **Files modified:** build.sh, install.sh
- **Verification:** `bash build.sh` exits 0 after stripping; `xxd` confirms LF-only endings
- **Committed in:** `b9ab45a` and `8938d86` (respective task commits)

---

**Total deviations:** 2 auto-fixed (1 bug in plan's command syntax, 1 blocking tooling issue)
**Impact on plan:** Both required for scripts to function. No scope creep.

## Issues Encountered
- `claude mcp list` outputs nothing in this environment (likely no active session context), but registration confirmed via `~/.claude.json` direct inspection

## User Setup Required
None - no external service configuration required.

## Next Phase Readiness
- All Phase 1 foundation scripts are complete and verified end-to-end
- `bash build.sh` from repo root builds native + managed in one command
- `bash install.sh` registers MCP at user scope — restart Claude Code to pick up
- Phase 2 can begin: C# type definitions and MCP tool stubs in DebuggerNetMcp.Core

---
*Phase: 01-foundation*
*Completed: 2026-02-22*

## Self-Check: PASSED

- build.sh: FOUND
- install.sh: FOUND
- 01-03-SUMMARY.md: FOUND
- Commit b9ab45a: FOUND
- Commit 8938d86: FOUND
- build.sh executable: PASSED
- install.sh executable: PASSED
- lib/libdotnetdbg.so exists: PASSED
