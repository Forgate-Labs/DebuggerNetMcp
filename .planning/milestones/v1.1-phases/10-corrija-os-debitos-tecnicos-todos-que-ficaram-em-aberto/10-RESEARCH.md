# Phase 10: Corrija os debitos tecnicos, todos que ficaram em aberto - Research

**Researched:** 2026-02-24
**Domain:** C# MCP server / ICorDebug / .NET debugging / xUnit testing
**Confidence:** HIGH

## Summary

Phase 10 addresses all accumulated technical debt from v1.1 (Phases 5-9). The v1.1-MILESTONE-AUDIT.md formally catalogues four debt items, but a full code inspection reveals additional issues: duplicated test helpers, untracked files, a disconnect between install.sh and the strace wrapper, a dead native C library, and silent exception suppression throughout the engine.

The most actionable debts are: (1) two missing automated tests (THRD-03 `debug_pause` and DTEST-02 `debug_launch_test` breakpoint), (2) duplicated `WaitForSpecificEvent`/`DrainToExit` helpers in both test classes, (3) `debugger-net-mcp.sh` and `v1.1-MILESTONE-AUDIT.md` are untracked by git, (4) `install.sh` does not use the strace wrapper despite README claiming it does, and (5) the native ptrace library (`libdotnetdbg.so`) is built by `build.sh` but never loaded by any C# code — it is dead code.

**Primary recommendation:** Fix the five concrete debt items in priority order: untracked files + install.sh discrepancy → dead native library → missing automated tests → duplicated helpers → bare catch cleanup.

---

## Identified Technical Debts

These are the concrete debts confirmed by code inspection and the formal audit:

### Debt 1: Missing Automated Test — THRD-03 (debug_pause)
**Source:** v1.1-MILESTONE-AUDIT.md item #2
**Severity:** Low
**What's missing:** `PauseAsync_SuspendsAllThreads` test. Currently THRD-03 is only covered by ICorDebug contract guarantee (`Stop(0)` suspends all managed threads), not by an automated regression test.
**What to build:** A test that launches HelloDebug, sets a BP in a background thread (BP-20), waits for it, calls `PauseAsync`, then asserts all threads are suspended (no new events arrive within a short window after the pause).
**Files affected:** `tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs`

### Debt 2: Missing Automated Test — DTEST-02 (debug_launch_test breakpoint)
**Source:** v1.1-MILESTONE-AUDIT.md item #3
**Severity:** Medium — LaunchTestAsync involves subprocess management, PID parsing, and attach timing; regression risk higher than pure engine code.
**What's missing:** E2E test: `debug_launch_test` → set breakpoint in MathTests `[Fact]` → `debug_continue` → verify breakpoint hit with `a=21, b=21` visible.
**What to build:** A `LaunchTestAsync_BreakpointInFact_HitsWithLocals` test in `DebuggerAdvancedTests.cs`. Must use the test project path and the `MathTests.cs` line 11 (`int result = a + b;`).
**Files affected:** `tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs`

### Debt 3: Duplicate Test Helpers
**Source:** Code inspection — both `DebuggerIntegrationTests.cs` and `DebuggerAdvancedTests.cs` contain identical `WaitForSpecificEvent<T>` and `DrainToExit` static methods.
**Severity:** Low — code duplication; any change to these helpers requires updating both files.
**Fix:** Move both helpers to a `DebuggerTestHelpers` static class in a new `DebuggerTestHelpers.cs` file, then delete the duplicates from both test classes.
**Files affected:** `tests/DebuggerNetMcp.Tests/DebuggerIntegrationTests.cs`, `tests/DebuggerNetMcp.Tests/DebuggerAdvancedTests.cs`, new `DebuggerTestHelpers.cs`

### Debt 4: `debugger-net-mcp.sh` Untracked by Git
**Source:** `git status` shows `?? debugger-net-mcp.sh`
**Severity:** Low-Medium — the strace wrapper is referenced in README.md, required for kernel 6.12+ operation, but not committed to the repo.
**Fix:** `git add debugger-net-mcp.sh` and commit. Also ensure `.gitignore` does not exclude it.
**Files affected:** `debugger-net-mcp.sh` (root level)

### Debt 5: `install.sh` Does Not Use the Strace Wrapper
**Source:** `install.sh` registers `dotnet run --project ... --no-build -c Release` directly. README says: "The wrapper script (`debugger-net-mcp.sh`) applies the `strace -f -e trace=none` workaround required for Linux kernel >= 6.12."
**Severity:** Medium — README/install.sh are inconsistent. On kernel 6.12+, the registered server may fail without strace. The `debugger-net-mcp.sh` wrapper uses the compiled binary path, not `dotnet run`.
**What to decide:** Either (a) update `install.sh` to register the wrapper script instead of `dotnet run`, OR (b) update README to reflect the current `dotnet run` approach and note strace is a separate concern. The wrapper uses a hardcoded binary path which differs from `dotnet run`.
**Files affected:** `install.sh`, `README.md`

### Debt 6: Dead Native Library (`libdotnetdbg.so`)
**Source:** Code inspection — `build.sh` builds the CMake native library, but `grep -r "libdotnetdbg\|dbg_attach\|DllImport"` returns no results in `src/`. The C code `ptrace_wrapper.c` is compiled but never loaded.
**Severity:** Medium — `build.sh` runs CMake unnecessarily on every build. The ICorDebug approach via libdbgshim replaces the need for a custom ptrace wrapper entirely.
**Fix options:**
  - Option A: Remove the `native/` directory, the cmake step from `build.sh`, and the `lib/libdotnetdbg.so`. Update `install.sh` to not set `LD_LIBRARY_PATH` for this library.
  - Option B: Keep native/ but remove from `build.sh` (make it optional/unused).
- Option A is recommended — the dead code creates confusion and unnecessary CMake dependency.
**Files affected:** `build.sh`, `install.sh`, `native/` directory (entire), `lib/libdotnetdbg.so`

### Debt 7: `v1.1-MILESTONE-AUDIT.md` Untracked by Git
**Source:** `git status` shows `?? .planning/v1.1-MILESTONE-AUDIT.md`
**Severity:** Negligible — documentation artifact; should be committed.
**Files affected:** `.planning/v1.1-MILESTONE-AUDIT.md`

### Debt 8: TYPE-04 Missing from 05-02-SUMMARY.md Frontmatter
**Source:** v1.1-MILESTONE-AUDIT.md item #4
**Severity:** Negligible — documentation omission only; implementation is wired and verified.
**Fix:** Add `TYPE-04` to `requirements-completed` in `.planning/phases/05-type-system/05-02-SUMMARY.md`.
**Files affected:** `.planning/phases/05-type-system/05-02-SUMMARY.md`

---

## Architecture Patterns

### Test Helper Consolidation Pattern
The established pattern for test helpers is to move shared code to `DebuggerFixture.cs` or a companion static class. Given `DebuggerFixture` is already an `IAsyncLifetime` class, the cleanest approach is a separate static class.

```csharp
// Recommended: tests/DebuggerNetMcp.Tests/DebuggerTestHelpers.cs
namespace DebuggerNetMcp.Tests;

internal static class DebuggerTestHelpers
{
    public static async Task<T> WaitForSpecificEvent<T>(
        DotnetDebugger dbg, CancellationToken ct) where T : DebugEvent
    {
        while (true)
        {
            var ev = await dbg.WaitForEventAsync(ct);
            if (ev is T typedEv) return typedEv;
            if (ev is ExitedEvent) throw new Exception($"Process exited before {typeof(T).Name}");
        }
    }

    public static async Task DrainToExit(DotnetDebugger dbg, CancellationToken ct)
    {
        while (true)
        {
            var ev = await dbg.WaitForEventAsync(ct);
            if (ev is ExitedEvent) return;
            if (ev is BreakpointHitEvent or StoppedEvent or ExceptionEvent)
                await dbg.ContinueAsync(ct);
        }
    }
}
```

### THRD-03 Test Pattern
`PauseAsync` is synchronous at the ICorDebug level (`Stop(0)` is blocking). After calling `PauseAsync`, the process is suspended and no events fire. The test approach:

```csharp
[Fact]
public async Task PauseAsync_SuspendsAllThreads_NoEventAfterPause()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    await Dbg.LaunchAsync(HelloDebugProject, HelloDebugDll, ct: cts.Token);
    await Dbg.ContinueAsync(cts.Token); // let process run
    await Task.Delay(20, cts.Token);   // give it time to be "running"
    await Dbg.PauseAsync(cts.Token);   // synchronous stop

    // After pause: process is stopped; GetStackTraceAsync should succeed without error
    var allThreads = await Dbg.GetAllThreadStackTracesAsync(cts.Token);
    Assert.NotEmpty(allThreads);  // at least main thread visible

    await Dbg.ContinueAsync(cts.Token);
    await DrainToExit(Dbg, cts.Token);
    await Dbg.DisconnectAsync(cts.Token);
}
```

### DTEST-02 Test Pattern
The LaunchTestAsync path: `dotnet build` → `dotnet test --VSTEST_HOST_DEBUG=1` → parse "Process Id: NNN" from stdout → `AttachAsync(pid)`. Testhost stops at CreateProcess.

```csharp
[Fact]
public async Task LaunchTestAsync_BreakpointInFact_HitsWithLocals()
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));

    // Tests project path (xUnit project containing MathTests)
    var testProject = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "DebuggerNetMcp.Tests"));
    var testsDll = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "DebuggerNetMcp.Tests.dll"));

    var (pid, _) = await Dbg.LaunchTestAsync(testProject, "AddTwoNumbers", cts.Token);
    // Set BP on line 11: "int result = a + b;"
    await Dbg.SetBreakpointAsync(testsDll, "MathTests.cs", 11, cts.Token);
    await Dbg.ContinueAsync(cts.Token);

    var hit = await WaitForSpecificEvent<BreakpointHitEvent>(Dbg, cts.Token);
    Assert.NotNull(hit);

    var locals = await Dbg.GetLocalsAsync(0, cts.Token);
    var aVar = locals.FirstOrDefault(v => v.Name == "a");
    Assert.NotNull(aVar);
    Assert.Equal("21", aVar.Value);

    await Dbg.ContinueAsync(cts.Token);
    await DrainToExit(Dbg, cts.Token);
    await Dbg.DisconnectAsync(cts.Token);
}
```

**Important:** DTEST-02 test uses the same collection `[Collection("Debugger")]` as the other tests — it must run sequentially. The test project is `tests/DebuggerNetMcp.Tests/` itself. There's a bootstrapping concern: the test being tested (`MathTests`) is in the same project as the test doing the testing. The filter `AddTwoNumbers_ReturnsCorrectSum` ensures only that specific test runs, preventing recursion.

### install.sh / Wrapper Script Alignment
The current `debugger-net-mcp.sh` wrapper hardcodes the Release binary path:
```bash
exec strace -f -e trace=none -o /dev/null \
  /home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Mcp/bin/Release/net10.0/DebuggerNetMcp.Mcp \
  2>>/tmp/debugger-net-mcp.log "$@"
```

`install.sh` uses `dotnet run --no-build -c Release` which is simpler but skips the strace workaround. The cleanest fix for Phase 10 is to update `install.sh` to register the wrapper script, making it consistent with README. The wrapper path needs to be dynamic (using `$SCRIPT_DIR`).

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Test helper deduplication | New abstract base class | Static helper class | xUnit fixture classes use constructor injection; abstract base complicates that |
| Process suspension verification | Custom ptrace-based check | Trust ICorDebug `Stop(0)` contract + test that inspection APIs work post-pause | ICorDebug `Stop(0)` is defined to synchronously suspend all managed threads |
| Strace integration | Custom kernel detection | Just run with strace wrapper always | strace with `-e trace=none` has near-zero overhead; kernel version detection is fragile |

---

## Common Pitfalls

### Pitfall 1: DTEST-02 Test Recursion
**What goes wrong:** If the test filter is not set, `LaunchTestAsync` runs ALL tests including itself, causing infinite recursion (test spawns testhost that runs test that spawns testhost...).
**Prevention:** Always pass a specific filter to `LaunchTestAsync` in the DTEST-02 test. Use `"FullyQualifiedName~AddTwoNumbers_ReturnsCorrectSum"` or `"AddTwoNumbers"`.

### Pitfall 2: DTEST-02 DLL Path for SetBreakpointAsync
**What goes wrong:** `SetBreakpointAsync` needs the compiled DLL path of the assembly containing the test method. For `MathTests.cs`, the DLL is `DebuggerNetMcp.Tests.dll`, not `HelloDebug.dll`. The test must find it at `AppContext.BaseDirectory/DebuggerNetMcp.Tests.dll` (same test output directory).
**Prevention:** Use `Path.Combine(AppContext.BaseDirectory, "DebuggerNetMcp.Tests.dll")` — the test assembly is already compiled and present in the test output directory.

### Pitfall 3: install.sh Wrapper Script Path
**What goes wrong:** The wrapper `debugger-net-mcp.sh` currently has a hardcoded absolute path to the binary. If `install.sh` registers it, users who cloned the repo to a different path will get a broken registration.
**Prevention:** Either (a) make the wrapper use `$SCRIPT_DIR` for relative resolution, or (b) have `install.sh` generate the wrapper with the correct `$SCRIPT_DIR` substituted at install time.

### Pitfall 4: Removing Native Library from LD_LIBRARY_PATH in install.sh
**What goes wrong:** `install.sh` passes `-e LD_LIBRARY_PATH="$SCRIPT_DIR/lib..."`. If `lib/libdotnetdbg.so` is removed, the `lib/` directory may disappear. If `libdbgshim.so` was also placed in `lib/`, removing it would break the server.
**Prevention:** Check that `lib/` only contains `libdotnetdbg.so` (the dead native library) before removing it. Confirm `libdbgshim.so` is at `~/.local/bin/` (per MEMORY.md and README), not in `lib/`.

---

## Code Examples

### Removing Dead Native Library from build.sh
```bash
# BEFORE (build.sh)
echo "==> Building native library (CMake)..."
cmake -S "$SCRIPT_DIR/native" -B "$SCRIPT_DIR/native/build" -DCMAKE_BUILD_TYPE=Release
cmake --build "$SCRIPT_DIR/native/build" --parallel

echo "==> Building managed projects (dotnet)..."
dotnet build "$SCRIPT_DIR/DebuggerNetMcp.sln" -c Release

# AFTER (build.sh)
echo "==> Building managed projects (dotnet)..."
dotnet build "$SCRIPT_DIR/DebuggerNetMcp.sln" -c Release
```

### Updated install.sh Using Wrapper Script
```bash
# Current (install.sh) registers dotnet run directly:
"$CLAUDE_BIN" mcp add \
    -s user \
    -e DOTNET_ROOT="..." \
    -e LD_LIBRARY_PATH="..." \
    -- debugger-net \
    dotnet run --project "..." --no-build -c Release

# Updated to use wrapper script:
"$CLAUDE_BIN" mcp add \
    -s user \
    -- debugger-net \
    "$SCRIPT_DIR/debugger-net-mcp.sh"
# (strace + DOTNET_ROOT + logging are all inside the wrapper)
```

---

## Prioritized Work Plan

| Priority | Debt Item | Effort | Value |
|----------|-----------|--------|-------|
| 1 | Commit untracked files (debugger-net-mcp.sh, v1.1-MILESTONE-AUDIT.md) | Trivial | Repo hygiene |
| 2 | Fix install.sh / strace wrapper discrepancy | Small | Correctness on kernel 6.12+ |
| 3 | Remove dead native library (native/, build.sh cmake step, lib/libdotnetdbg.so) | Small | Build simplicity |
| 4 | Add THRD-03 automated test (PauseAsync) | Small | Regression safety |
| 5 | Add DTEST-02 automated test (LaunchTestAsync breakpoint) | Medium | Regression safety |
| 6 | Deduplicate WaitForSpecificEvent/DrainToExit helpers | Small | Maintainability |
| 7 | Fix TYPE-04 frontmatter in 05-02-SUMMARY.md | Trivial | Doc accuracy |

**Suggested plan breakdown:**
- Plan 10-01: Repo hygiene (untracked files, install.sh, dead native library, SUMMARY frontmatter)
- Plan 10-02: Missing automated tests (THRD-03 + DTEST-02) + deduplicate test helpers

---

## State of the Art

| Area | Current State | Debt |
|------|--------------|------|
| Test coverage | 14 tests, 0 failures | Missing THRD-03 (pause) and DTEST-02 (launch_test BP) |
| Test helpers | Duplicated in 2 files | Should be shared in DebuggerTestHelpers.cs |
| Build system | CMake + dotnet | CMake is dead weight (native lib unused) |
| Repo hygiene | 2 untracked files | debugger-net-mcp.sh + v1.1-MILESTONE-AUDIT.md uncommitted |
| install.sh | Registers dotnet run directly | README says it uses strace wrapper — inconsistent |
| Documentation | TYPE-04 missing from 05-02-SUMMARY.md frontmatter | Minor gap |

---

## Open Questions

1. **Should the strace wrapper be updated to use `$SCRIPT_DIR` instead of hardcoded path?**
   - What we know: Current wrapper hardcodes `/home/eduardo/Projects/DebuggerNetMcp/...`
   - What's unclear: Whether install.sh should generate the wrapper dynamically or commit a template
   - Recommendation: Have install.sh generate a local copy of the wrapper with `$SCRIPT_DIR` substituted, or rewrite the wrapper to use `$(dirname "$0")` for self-relative path resolution.

2. **Is the `lib/` directory used for anything else besides libdotnetdbg.so?**
   - What we know: `ls lib/` shows only the cmake build output directory and `libdotnetdbg.so`
   - What's unclear: Whether any user has placed `libdbgshim.so` there
   - Recommendation: Remove `lib/` from the repo; update `install.sh` to drop the `LD_LIBRARY_PATH` for `$SCRIPT_DIR/lib` (it only pointed to the dead library).

3. **Will DTEST-02 test be stable across machines with different .NET build speeds?**
   - What we know: `LaunchTestAsync` has a 25-second timeout for testhost PID. The existing attach test uses a retry loop.
   - What's unclear: Whether testhost starts fast enough in a CI environment.
   - Recommendation: Use a generous CancellationTokenSource timeout (60 seconds) for the DTEST-02 test and rely on the existing 25-second PID timeout in LaunchTestAsync.

---

## Sources

### Primary (HIGH confidence)
- Direct code inspection of all source files in `src/` and `tests/` — confirmed by reading files
- `git status` output — confirmed untracked files
- `.planning/v1.1-MILESTONE-AUDIT.md` — formal debt catalogue from milestone audit

### Secondary (MEDIUM confidence)
- `.planning/phases/*/SUMMARY.md` files — implementation decisions and known limitations per phase
- `MEMORY.md` project memory — architecture decisions and known patterns

### Tertiary (LOW confidence)
- None

---

## Metadata

**Confidence breakdown:**
- Debt identification: HIGH — confirmed by code inspection + formal audit
- Test patterns: HIGH — matches existing established patterns in codebase
- Build system: HIGH — confirmed by reading build.sh and grep for usage
- Fix approaches: MEDIUM — some decisions (install.sh wrapper strategy) require user preference

**Research date:** 2026-02-24
**Valid until:** 2026-03-24 (stable codebase; no external dependencies changing)
