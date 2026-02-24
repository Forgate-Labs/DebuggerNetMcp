---
phase: 10-corrija-os-debitos-tecnicos-todos-que-ficaram-em-aberto
plan: "01"
subsystem: infra
tags: [shell-scripts, build-system, cmake, strace, icordebug, portability]

dependency_graph:
  requires: []
  provides:
    - portable strace wrapper (SCRIPT_DIR-based, no hardcoded paths)
    - install.sh registers wrapper directly (kernel 6.12+ compatible)
    - build.sh managed-only (no CMake)
    - native/ and lib/ removed from repo
    - v1.1-MILESTONE-AUDIT.md committed
    - TYPE-04 recorded in 05-02-SUMMARY frontmatter
  affects: [install, build, developer-setup]

tech-stack:
  added: []
  patterns:
    - "SCRIPT_DIR-relative binary resolution in shell scripts"

key-files:
  created:
    - debugger-net-mcp.sh
    - .planning/v1.1-MILESTONE-AUDIT.md
  modified:
    - install.sh
    - build.sh
    - .planning/phases/05-type-system/05-02-SUMMARY.md

key-decisions:
  - "debugger-net-mcp.sh uses SCRIPT_DIR + DOTNET_ROOT export so binary resolves correctly when spawned by Claude"
  - "install.sh registers wrapper script directly — no env var passthrough needed (wrapper handles it)"
  - "build.sh CMake block removed entirely — native/ was dead since Phase 3 (ICorDebug replaced ptrace wrapper)"

patterns-established:
  - "Shell scripts use SCRIPT_DIR=(cd dirname BASH_SOURCE && pwd) for portability"

requirements-completed:
  - INFRA-04
  - INFRA-05
  - NATIVE-01
  - NATIVE-02

duration: 2min
completed: "2026-02-24"
---

# Phase 10 Plan 01: Repo Hygiene and Build System Cleanup Summary

**Portable strace wrapper committed, install.sh updated to register it, CMake/native artifacts removed, and TYPE-04 SUMMARY frontmatter corrected — all v1.1 repo hygiene debt cleared**

## Performance

- **Duration:** ~2 min
- **Started:** 2026-02-24T18:38:49Z
- **Completed:** 2026-02-24T18:40:24Z
- **Tasks:** 3
- **Files modified:** 5

## Accomplishments
- Fixed `debugger-net-mcp.sh` to use `SCRIPT_DIR` instead of a hardcoded `/home/eduardo` path — now portable for any install location
- Updated `install.sh` to register the strace wrapper directly; removed `-e DOTNET_ROOT`/`-e LD_LIBRARY_PATH` passthrough (wrapper handles it internally)
- Removed CMake block from `build.sh`; `native/` directory and `lib/libdotnetdbg.so` deleted — dead since Phase 3 replaced ptrace approach with ICorDebug
- Committed `debugger-net-mcp.sh` and `.planning/v1.1-MILESTONE-AUDIT.md` (both were untracked)
- Added `TYPE-04` to `05-02-SUMMARY.md` requirements-completed frontmatter (static field reading was implemented in Plan 05-02 but omitted)

## Task Commits

Each task was committed atomically:

1. **Task 1: Fix debugger-net-mcp.sh portability and commit untracked files** - `98e20c3` (chore)
2. **Task 2: Update install.sh, remove CMake from build.sh, remove native/ and lib/** - `740def9` (chore)
3. **Task 3: Fix TYPE-04 frontmatter in 05-02-SUMMARY.md** - `03a55e8` (docs)

## Files Created/Modified
- `debugger-net-mcp.sh` - Portable strace wrapper using SCRIPT_DIR; adds DOTNET_ROOT export
- `install.sh` - Now registers `debugger-net-mcp.sh` wrapper instead of `dotnet run`
- `build.sh` - CMake block removed; builds managed solution only via `dotnet build`
- `.planning/v1.1-MILESTONE-AUDIT.md` - Committed (was untracked)
- `.planning/phases/05-type-system/05-02-SUMMARY.md` - Added `TYPE-04` to `requirements_completed`
- `native/CMakeLists.txt` + `native/src/ptrace_wrapper.c` - Deleted (dead code)

## Decisions Made
- `debugger-net-mcp.sh` exports `DOTNET_ROOT` with `HOME/.dotnet` fallback — necessary because Claude spawns the MCP process without inheriting the shell environment
- `install.sh` no longer passes `-e DOTNET_ROOT` or `-e LD_LIBRARY_PATH` to `claude mcp add` — the wrapper script owns all environment setup, keeping install.sh simpler
- `native/build/` (untracked CMake output) removed from filesystem along with tracked source files

## Deviations from Plan

None — plan executed exactly as written. The only minor discovery was that `lib/libdotnetdbg.so` was untracked by git (not staged for `git rm`), so it was removed directly with `rm -rf lib/`.

## Issues Encountered
- `git rm lib/libdotnetdbg.so` returned fatal (file not tracked) — the `.so` binary was never committed. Removed it from the filesystem directly with `rm -rf lib/`. Not a blocker.

## User Setup Required
None — no external service configuration required.

## Next Phase Readiness
- Repo hygiene complete: no untracked project files, no dead build artifacts, install.sh works out-of-the-box on kernel 6.12+
- Ready for Plan 10-02 (next tech debt item)

## Self-Check: PASSED

All files exist and all task commits verified in git history:
- `debugger-net-mcp.sh` — FOUND (commit 98e20c3)
- `install.sh`, `build.sh` — FOUND (commit 740def9)
- `.planning/v1.1-MILESTONE-AUDIT.md` — FOUND (commit 98e20c3)
- `.planning/phases/05-type-system/05-02-SUMMARY.md` — FOUND (commit 03a55e8)
- Build passes: `./build.sh` completes with `dotnet build -c Release` only

---
*Phase: 10-corrija-os-debitos-tecnicos-todos-que-ficaram-em-aberto*
*Completed: 2026-02-24*
