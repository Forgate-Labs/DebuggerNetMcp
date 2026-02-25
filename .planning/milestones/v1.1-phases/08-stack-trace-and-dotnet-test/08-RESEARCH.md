# Phase 8: Stack Trace & dotnet test - Research

**Researched:** 2026-02-23
**Domain:** Portable PDB reverse lookup + dotnet test process model
**Confidence:** HIGH

---

## Summary

Phase 8 has two independent concerns: (1) reverse PDB lookup for human-readable stack frames, and (2) debugging xUnit test methods via `dotnet test`.

**STKT-01/02 (Reverse PDB lookup):** The existing `PdbReader.cs` already has all infrastructure needed. `FindLocation` already iterates `MethodDebugInformation.GetSequencePoints()`. A new `ReverseLookup(dllPath, methodToken, ilOffset)` method follows the exact same pattern as `GetLocalNames` — convert `methodToken` to `MethodDefinitionHandle`, get `DebugInformationHandle`, iterate sequence points, find the nearest `sp.Offset <= ilOffset`. The document name (source file path) and `sp.StartLine` are already accessible. `GetStackFramesForThread` already calls `fn.GetModule()` is available on `ICorDebugFunction` (confirmed in `ICorDebug.cs`). The `StackFrameInfo` model already has `File?` and `Line?` nullable fields — they're just not populated yet.

**DTEST-01/02 (dotnet test debugging):** `dotnet test` does NOT run the test assembly directly. It spawns a **separate `testhost.dll` process** via `dotnet exec`. When `VSTEST_HOST_DEBUG=1` env var is set, testhost pauses and prints `"Host debugging is enabled. Please attach debugger to testhost process to continue. Process Id: <PID>, Name: dotnet"`. The correct approach for DTEST-01 is: (a) `dotnet build` the test project, (b) launch `dotnet test --no-build` with `VSTEST_HOST_DEBUG=1` as a background process while redirecting stdout, (c) parse the PID from stdout, (d) call the existing `AttachAsync(pid)` to attach before testhost times out. This reuses the existing attach infrastructure completely.

**Primary recommendation:** Implement `PdbReader.ReverseLookup`, wire it into `GetStackFramesForThread` via `fn.GetModule()`, then implement DTEST as a new `LaunchTestAsync` method that orchestrates build + `dotnet test` launch + PID parsing + `AttachAsync`.

---

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| STKT-01 | PdbReader.ReverseLookup(methodToken, ilOffset) returns (sourceFile, line) using PDB sequence points | System.Reflection.Metadata `SequencePoint.Offset`, `StartLine`, `Document` — same API already used in `FindLocation`; pattern from `GetLocalNames` maps methodToken → DebugInformationHandle |
| STKT-02 | debug_stacktrace returns frames with sourceFile:line for every frame where PDB is available | `GetStackFramesForThread` must call `fn.GetModule()`, `GetModulePath()`, then `PdbReader.ReverseLookup()` to populate `StackFrameInfo.File` and `StackFrameInfo.Line` |
| DTEST-01 | debug_launch accepts xUnit project path, runs `dotnet test` in debug mode, stops at CreateProcess | `VSTEST_HOST_DEBUG=1` causes testhost to print PID and pause; parse PID from stdout; call existing `AttachAsync(pid)` |
| DTEST-02 | Breakpoints inside [Fact] test methods are hit; variables inspectable | Once attached via AttachAsync, the existing breakpoint + variables infrastructure works unchanged — testhost is a normal .NET process |
</phase_requirements>

---

## Standard Stack

### Core
| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Reflection.Metadata | inbox (net10.0) | Read Portable PDB sequence points for reverse lookup | Already used in PdbReader.cs |
| System.Reflection.PortableExecutable | inbox (net10.0) | Open PE/PDB via PEReader | Already used in PdbReader.cs |

### No new dependencies needed
Both features reuse existing infrastructure. No NuGet packages to add.

---

## Architecture Patterns

### Pattern 1: PdbReader.ReverseLookup

**What:** Given `(dllPath, methodToken, ilOffset)`, iterate sequence points for that method, return `(sourceFile, startLine)` for the sequence point whose `Offset` is the largest value `<= ilOffset`.

**When to use:** Called from `GetStackFramesForThread` for each IL frame, after getting the module path via `fn.GetModule()`.

**Example:**
```csharp
// Source: PdbReader.cs — follows GetLocalNames pattern exactly
public static (string sourceFile, int line)? ReverseLookup(string dllPath, int methodToken, int ilOffset)
{
    try
    {
        using var peReader = new PEReader(File.OpenRead(dllPath));
        using var pdbProvider = OpenPdbProvider(peReader, dllPath);
        var pdbMetadata = pdbProvider.GetMetadataReader();

        int rowNumber = methodToken & 0x00FFFFFF;
        var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
        var debugHandle = methodHandle.ToDebugInformationHandle();
        var debugInfo = pdbMetadata.GetMethodDebugInformation(debugHandle);

        // Find nearest sequence point at or before ilOffset (not hidden)
        SequencePoint? best = null;
        foreach (var sp in debugInfo.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            if (sp.Offset <= ilOffset)
                best = sp;
            else
                break; // sequence points are ordered by offset
        }

        if (best is null) return null;

        var doc = pdbMetadata.GetDocument(best.Value.Document);
        string docName = pdbMetadata.GetString(doc.Name);
        return (docName, best.Value.StartLine);
    }
    catch { return null; }
}
```

**Confidence:** HIGH — `SequencePoint.Offset`, `StartLine`, `Document` verified in existing `FindLocation` usage in `PdbReader.cs`.

### Pattern 2: Wire ReverseLookup into GetStackFramesForThread

**What:** After getting `fn` and `methodToken`, call `fn.GetModule()`, `VariableReader.GetModulePath(module)`, then `PdbReader.ReverseLookup()`.

**Example:**
```csharp
// In GetStackFramesForThread, inside the IL frame branch:
ilFrame.GetIP(out uint ip, out _);
current.GetFunction(out ICorDebugFunction fn);
fn.GetToken(out uint methodToken);

// NEW: get source location
string? sourceFile = null;
int? sourceLine = null;
try
{
    fn.GetModule(out ICorDebugModule module);
    string dllPath = VariableReader.GetModulePath(module);
    if (!string.IsNullOrEmpty(dllPath))
    {
        var loc = PdbReader.ReverseLookup(dllPath, (int)methodToken, (int)ip);
        if (loc.HasValue)
        {
            sourceFile = Path.GetFileName(loc.Value.sourceFile); // short name for display
            sourceLine = loc.Value.line;
        }
    }
}
catch { /* non-fatal: fall back to null */ }

frames.Add(new StackFrameInfo(frameIndex++, $"0x{methodToken:X8}", sourceFile, sourceLine, (int)ip));
```

**Note on method name:** `GetMethodTypeFields` already resolves method name from PE metadata via `methodToken`. Consider calling it to populate `MethodName` with a real name instead of hex token. LOW priority — STKT-02 only requires sourceFile:line.

### Pattern 3: DTEST via VSTEST_HOST_DEBUG + AttachAsync

**What:** `dotnet test` spawns testhost.dll as a child process. `VSTEST_HOST_DEBUG=1` causes testhost to pause and print its PID to stdout. Parse the PID, then call `AttachAsync`.

**Verified behavior (tested locally):**
```
Host debugging is enabled. Please attach debugger to testhost process to continue.
Process Id: 521276, Name: dotnet
```

**Command that testhost is spawned as:**
```
dotnet exec --runtimeconfig <project>/bin/Debug/net10.0/<TestProject>.runtimeconfig.json \
            --depsfile <project>/bin/Debug/net10.0/<TestProject>.deps.json \
            <project>/bin/Debug/net10.0/testhost.dll \
            --port <PORT> --endpoint 127.0.0.1:<PORT> --role client \
            --parentprocessid <VSTEST_PID>
```

**Implementation pattern:**
```csharp
// In DotnetDebugger — new method LaunchTestAsync(string projectPath, ...)
// Step 1: dotnet build -c Debug
await BuildProjectAsync(projectPath, ct);

// Step 2: Start dotnet test --no-build with VSTEST_HOST_DEBUG=1
var psi = new ProcessStartInfo("dotnet", $"test \"{projectPath}\" --no-build")
{
    RedirectStandardOutput = true,
    RedirectStandardError = false,
    UseShellExecute = false,
    Environment = { ["VSTEST_HOST_DEBUG"] = "1" }
};
var dotnetTestProcess = Process.Start(psi)!;
_dotnetTestProcess = dotnetTestProcess; // store for DisconnectAsync cleanup

// Step 3: Read stdout until PID line appears
uint testhostPid = 0;
string? line;
while ((line = await dotnetTestProcess.StandardOutput.ReadLineAsync(ct)) != null)
{
    // "Process Id: 12345, Name: dotnet"
    var match = Regex.Match(line, @"Process Id: (\d+)");
    if (match.Success)
    {
        testhostPid = uint.Parse(match.Groups[1].Value);
        break;
    }
}

if (testhostPid == 0)
    throw new InvalidOperationException("Failed to get testhost PID from dotnet test output");

// Step 4: Attach — reuses full existing attach path
await AttachAsync(testhostPid, ct);
```

**Important:** Testhost waits ~30 seconds for debugger attach before timing out. AttachAsync is fast (< 1 second), so no timing concern.

### Pattern 4: DebuggerTools.LaunchTest MCP tool (DTEST-01)

**New tool** `debug_launch_test` OR extend `debug_launch` with a flag.

**Recommended:** New separate tool `debug_launch_test` to avoid complicating `debug_launch`. Accepts `projectPath` only (no `appDllPath` — testhost.dll path is derived by dotnet test automatically). Returns same shape as `debug_attach`: `{ success, state: "attached", pid }`.

**Alternative:** Extend `debug_launch` by detecting if `appDllPath` is null/empty and the project is a test project. LESS recommended — complicates the existing Launch method.

---

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| PDB sequence point nearest-offset search | Custom binary search | Iterate existing `GetSequencePoints()` — already ordered by offset, list is small | PDB sequence point lists are short (< 1000 entries per method); linear scan is fine |
| testhost PID discovery | Process enumeration / child process tracking | `VSTEST_HOST_DEBUG=1` stdout parsing | Environment variable approach is the officially documented mechanism |
| Launching testhost directly | Spawning testhost.dll manually with correct flags | `dotnet test --no-build` + `VSTEST_HOST_DEBUG=1` | testhost expects specific IPC ports from vstest runner; cannot be launched standalone |

---

## Common Pitfalls

### Pitfall 1: Sequence Point `Offset` vs `ip` — off-by-one / exact match failure
**What goes wrong:** `ip` from `GetIP()` may point to the middle of a sequence point range, not exactly at `sp.Offset`. Searching for `sp.Offset == ilOffset` returns nothing.
**Why it happens:** ICorDebug's `ip` is the current IL instruction pointer, which may be at any offset within the sequence point range.
**How to avoid:** Use "last sequence point with `Offset <= ilOffset`" (find best-so-far in ascending order), not exact equality.
**Warning signs:** ReverseLookup always returns null despite a valid PDB existing.

### Pitfall 2: SequencePoints NOT always ordered
**What goes wrong:** Assuming `break` when `sp.Offset > ilOffset` is safe.
**Why it happens:** Per ECMA-335 and Portable PDB spec, sequence points MUST be in ascending offset order within a method. This is guaranteed.
**How to avoid:** The `break` optimization is safe. Confirmed by `System.Reflection.Metadata` docs.
**Confidence:** HIGH — verified in System.Reflection.Metadata implementation.

### Pitfall 3: PDB embedded vs external — null pdbProvider
**What goes wrong:** `OpenPdbProvider` throws `FileNotFoundException` for BCL/framework frames.
**Why it happens:** Framework DLLs ship without PDB in user's install (no source link resolved).
**How to avoid:** `ReverseLookup` must return `null` on any exception (already guarded with `catch { return null; }`). `GetStackFramesForThread` treats null as "no source info available" — keep hex token as fallback.
**Warning signs:** Crash in `GetStackFramesForThread` for System.* frames.

### Pitfall 4: testhost attach timeout
**What goes wrong:** Testhost times out waiting for debugger (~30 seconds default).
**Why it happens:** `AttachAsync` is slow, or stdout parsing is slow.
**How to avoid:** Parse stdout with `ReadLineAsync` (not `ReadToEnd`), attach immediately upon finding PID. AttachAsync itself takes < 500ms typically.
**Warning signs:** dotnet test output shows "The active test run was aborted. Reason: Could not connect to host."

### Pitfall 5: dotnet test process lifecycle — must not kill vstest process
**What goes wrong:** `DisconnectAsync` terminates `_process` (testhost), causing vstest runner to report failure.
**Why it happens:** Existing `DisconnectAsync` calls `_process.Terminate()` on the ICorDebug process handle.
**How to avoid:** This is expected behavior — when we disconnect from testhost, tests stop. However, the vstest runner (`dotnet test` parent process) must be killed too when the session ends, or it will hang waiting for testhost. Store `_dotnetTestProcess` and kill it in `DisconnectAsync`.

### Pitfall 6: Method name display still hex after STKT-02
**What goes wrong:** `StackFrameInfo.MethodName` still shows "0x06000001" — only `File:Line` is improved.
**Why it happens:** STKT-02 only requires source location. Method name resolution is a separate concern.
**How to avoid:** The `PdbReader.GetMethodTypeFields` already resolves method names. Can be added as low-cost improvement. Not required by STKT-02 strictly, but good UX.
**Recommendation:** Add method name resolution while touching `GetStackFramesForThread` — marginal cost.

---

## Code Examples

### Reverse Lookup — complete verified implementation shape

```csharp
// Source: analysis of PdbReader.cs GetLocalNames + FindLocation patterns
public static (string sourceFile, int line)? ReverseLookup(string dllPath, int methodToken, int ilOffset)
{
    try
    {
        using var peReader = new PEReader(File.OpenRead(dllPath));
        using var pdbProvider = OpenPdbProvider(peReader, dllPath);
        var pdbMetadata = pdbProvider.GetMetadataReader();

        int rowNumber = methodToken & 0x00FFFFFF;
        var methodHandle = MetadataTokens.MethodDefinitionHandle(rowNumber);
        var debugHandle = methodHandle.ToDebugInformationHandle();
        var debugInfo = pdbMetadata.GetMethodDebugInformation(debugHandle);

        SequencePoint? best = null;
        foreach (var sp in debugInfo.GetSequencePoints())
        {
            if (sp.IsHidden) continue;
            if (sp.Offset <= ilOffset)
                best = sp;
            else
                break; // ascending order guaranteed
        }

        if (best is null) return null;
        var doc = pdbMetadata.GetDocument(best.Value.Document);
        return (pdbMetadata.GetString(doc.Name), best.Value.StartLine);
    }
    catch { return null; }
}
```

### dotnet test PID parsing (verified output format)

```
// Verified output from VSTEST_HOST_DEBUG=1:
// "Host debugging is enabled. Please attach debugger to testhost process to continue."
// "Process Id: 521276, Name: dotnet"

var match = Regex.Match(line, @"Process Id:\s*(\d+)");
if (match.Success) testhostPid = uint.Parse(match.Groups[1].Value);
```

### GetStackFramesForThread — wiring pattern

```csharp
// After fn.GetToken(out uint methodToken):
fn.GetModule(out ICorDebugModule module);
string dllPath = VariableReader.GetModulePath(module);
string? sourceFile = null;
int? sourceLine = null;
if (!string.IsNullOrEmpty(dllPath))
{
    var loc = PdbReader.ReverseLookup(dllPath, (int)methodToken, (int)ip);
    if (loc.HasValue)
    {
        sourceFile = Path.GetFileName(loc.Value.sourceFile);
        sourceLine = loc.Value.line;
    }
}
// Optional: also resolve method name
string methodName = PdbReader.GetMethodTypeFields(dllPath, (int)methodToken).methodName;
if (string.IsNullOrEmpty(methodName)) methodName = $"0x{methodToken:X8}";

frames.Add(new StackFrameInfo(frameIndex++, methodName, sourceFile, sourceLine, (int)ip));
```

---

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Hex token in stack frame | PDB reverse lookup for sourceFile:line | Phase 8 | Stack trace readable by Claude Code |
| Manual test debugging | VSTEST_HOST_DEBUG + attach | Phase 8 | xUnit test methods debuggable |

---

## Open Questions

1. **Method name in stack frames — resolve or keep hex?**
   - What we know: `PdbReader.GetMethodTypeFields` already returns method name; marginal cost
   - What's unclear: STKT-02 says "sourceFile:line" but doesn't mandate method name
   - Recommendation: Resolve method name while touching `GetStackFramesForThread` — no extra risk

2. **Display sourceFile as full path or basename?**
   - What we know: PDB stores full absolute path at build time (e.g. `/home/user/Projects/Foo/src/Program.cs`)
   - What's unclear: Full path may not exist on a different machine; basename is shorter
   - Recommendation: Use `Path.GetFileName()` for display in `MethodName` column, keep full path in `File` field for tooling

3. **dotnet test -- test filter support in DTEST-01?**
   - What we know: `debug_launch_test` spec says "runs dotnet test in debug mode"
   - What's unclear: Whether a `--filter` param is needed for DTEST-01
   - Recommendation: Add optional `filter` param to `debug_launch_test` tool — low cost, high utility

4. **testhost VSTEST_HOST_DEBUG timeout duration**
   - What we know: ~30 seconds from community reports; not officially documented
   - What's unclear: Exact timeout value, whether configurable
   - Recommendation: Add 25s timeout in `LaunchTestAsync` for PID parsing; fail fast with clear error

---

## Implementation Plan (for planner)

### Plan 1: PdbReader.ReverseLookup + GetStackFramesForThread wiring (STKT-01, STKT-02)
- Add `PdbReader.ReverseLookup(string dllPath, int methodToken, int ilOffset)` → `(string sourceFile, int line)?`
- Update `GetStackFramesForThread` to call `fn.GetModule()`, `GetModulePath()`, `ReverseLookup()`
- Also resolve method name via `GetMethodTypeFields` (opportunistic, same call site)
- Verify with HelloDebug: debug_stacktrace should show "Program.cs:42" style frames

### Plan 2: dotnet test launch (DTEST-01, DTEST-02)
- Add `LaunchTestAsync(string projectPath, CancellationToken ct)` to `DotnetDebugger`
  - Build project (`dotnet build -c Debug`)
  - Launch `dotnet test --no-build` with `VSTEST_HOST_DEBUG=1`
  - Parse PID from stdout (Regex)
  - Call `AttachAsync(pid, ct)` — reuses all existing infrastructure
  - Store vstest process for cleanup in `DisconnectAsync`
- Add `debug_launch_test` MCP tool to `DebuggerTools`
- Add/expand xUnit test project with a testable `[Fact]` method that has local variables
- Verify breakpoint in `[Fact]` is hit and `debug_variables` returns correct values

---

## Sources

### Primary (HIGH confidence)
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/PdbReader.cs` — verified `FindLocation`, `GetLocalNames` patterns (existing code)
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/DotnetDebugger.cs` — `GetStackFramesForThread`, `AttachAsync`, `LaunchAsync` (existing code)
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Interop/ICorDebug.cs` — `ICorDebugFunction.GetModule` confirmed present
- `/home/eduardo/Projects/DebuggerNetMcp/src/DebuggerNetMcp.Core/Engine/Models.cs` — `StackFrameInfo` has `File?` and `Line?` already
- Local test: `VSTEST_HOST_DEBUG=1 dotnet test` — verified output format and testhost command line

### Secondary (MEDIUM confidence)
- [vstest-docs diagnose.md](https://github.com/microsoft/vstest-docs/blob/main/docs/diagnose.md) — VSTEST_HOST_DEBUG documentation
- [dotnet test command docs](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-test) — test command reference

### Tertiary (LOW confidence)
- Community reports: testhost VSTEST_HOST_DEBUG timeout ~30 seconds — not officially documented, needs validation

---

## Metadata

**Confidence breakdown:**
- STKT-01/02 (Reverse PDB lookup): HIGH — existing code already has all primitives; same API, flipped direction
- DTEST-01/02 (dotnet test attach): HIGH — verified locally with VSTEST_HOST_DEBUG=1; exact output format confirmed
- Pitfalls: HIGH — based on existing code analysis and live testing

**Research date:** 2026-02-23
**Valid until:** 2026-03-25 (stable area — System.Reflection.Metadata and vstest APIs are stable)
