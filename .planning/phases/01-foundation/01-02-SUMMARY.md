---
phase: 01-foundation
plan: 02
subsystem: infra
tags: [cmake, c, ptrace, native, shared-library, linux]

# Dependency graph
requires: []
provides:
  - "native/CMakeLists.txt: CMake build definition producing libdotnetdbg.so"
  - "native/src/ptrace_wrapper.c: 5 ptrace wrapper functions with PTRACE_SEIZE"
  - "libdotnetdbg.so: compiled shared library (build artifact, not committed)"
affects: [phase-2, phase-3, debug-engine]

# Tech tracking
tech-stack:
  added: [cmake-3.20, gcc-13, ptrace-seize-api]
  patterns:
    - "__attribute__((visibility(default))) + -fvisibility=hidden for controlled symbol export"
    - "PTRACE_SEIZE (not PTRACE_ATTACH) for kernel 6.12+ safe process attachment"
    - "Out-of-source CMake build (native/build/) with output to lib/ at repo root"

key-files:
  created:
    - native/CMakeLists.txt
    - native/src/ptrace_wrapper.c
  modified:
    - .gitignore

key-decisions:
  - "PTRACE_SEIZE used in dbg_attach — does not stop process on attach unlike PTRACE_ATTACH (kernel 6.12+ safe)"
  - "LIBRARY_OUTPUT_DIRECTORY set to ${CMAKE_SOURCE_DIR}/../lib to avoid copy step in build.sh"
  - "Added #include <stddef.h> for NULL (GCC 13 strict C mode requires explicit include)"
  - "native/build/ and lib/ added to .gitignore — build artifacts not committed"

patterns-established:
  - "Pattern: CMake shared library with C_VISIBILITY_PRESET hidden + explicit EXPORT macro"
  - "Pattern: ptrace wrapper returns -errno on failure, 0 or positive on success"

requirements-completed: [NATIVE-01, NATIVE-02]

# Metrics
duration: 8min
completed: 2026-02-22
---

# Phase 1 Plan 02: Native CMake ptrace Wrapper Summary

**CMake shared library (libdotnetdbg.so) with 5 PTRACE_SEIZE-based ptrace wrapper functions, verified via nm -D showing exactly 5 exported symbols**

## Performance

- **Duration:** 8 min
- **Started:** 2026-02-22T21:40:14Z
- **Completed:** 2026-02-22T21:48:00Z
- **Tasks:** 2
- **Files modified:** 3

## Accomplishments

- Created `native/CMakeLists.txt` with hidden visibility, SHARED library target, and output to `lib/`
- Created `native/src/ptrace_wrapper.c` implementing all 5 required functions using PTRACE_SEIZE
- Clean cmake build produces `lib/libdotnetdbg.so` with exactly 5 exported symbols confirmed via `nm -D`

## Task Commits

Each task was committed atomically:

1. **Task 1: Write CMakeLists.txt for libdotnetdbg.so** - `59e72ac` (feat)
2. **Task 2: Write ptrace_wrapper.c and verify build** - `cfda155` (feat)

**Plan metadata:** _(docs commit follows)_

## Files Created/Modified

- `native/CMakeLists.txt` - CMake build definition: SHARED library target, hidden visibility, LIBRARY_OUTPUT_DIRECTORY to lib/
- `native/src/ptrace_wrapper.c` - 5 exported ptrace functions (dbg_attach, dbg_detach, dbg_interrupt, dbg_continue, dbg_wait)
- `.gitignore` - Added native/build/ and lib/ exclusions

## Decisions Made

- PTRACE_SEIZE chosen for `dbg_attach` instead of PTRACE_ATTACH: does not send SIGSTOP, avoids race conditions with ICorDebug's libdbgshim callback on kernel 6.12+
- `LIBRARY_OUTPUT_DIRECTORY "${CMAKE_SOURCE_DIR}/../lib"` set in CMakeLists.txt to output directly to `lib/` without a copy step in build.sh

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Added missing `#include <stddef.h>` for NULL**
- **Found during:** Task 2 (ptrace_wrapper.c build)
- **Issue:** GCC 13 strict C mode: `NULL` undeclared — `<sys/ptrace.h>` on this system does not transitively include `<stddef.h>`
- **Fix:** Added `#include <stddef.h>` to `ptrace_wrapper.c` includes block
- **Files modified:** native/src/ptrace_wrapper.c
- **Verification:** `cmake --build` exits 0; `nm -D lib/libdotnetdbg.so | grep " T dbg_"` shows 5 symbols
- **Committed in:** cfda155 (Task 2 commit)

---

**Total deviations:** 1 auto-fixed (Rule 1 - missing include for NULL)
**Impact on plan:** Required for compilation. No scope creep.

## Issues Encountered

GCC 13 strict C mode requires explicit `#include <stddef.h>` for `NULL` — the plan's example C code omitted this include. Fixed inline per Rule 1 (auto-fix bug).

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

- `native/CMakeLists.txt` and `native/src/ptrace_wrapper.c` are committed and ready
- Building from clean state: `cmake -S native -B native/build -DCMAKE_BUILD_TYPE=Release && cmake --build native/build --parallel` produces `lib/libdotnetdbg.so`
- Symbol export verified: exactly 5 `dbg_*` symbols, PTRACE_SEIZE used, PTRACE_ATTACH absent
- Ready for Plan 03 (.NET solution scaffold) and eventually Phase 3 (C# P/Invoke into libdotnetdbg.so)

---
*Phase: 01-foundation*
*Completed: 2026-02-22*

## Self-Check: PASSED

- native/CMakeLists.txt: FOUND
- native/src/ptrace_wrapper.c: FOUND
- 01-02-SUMMARY.md: FOUND
- Commit 59e72ac: FOUND
- Commit cfda155: FOUND
- native/build/ in .gitignore: FOUND
- lib/ in .gitignore: FOUND
- Exported symbols: 5 (PASS)
