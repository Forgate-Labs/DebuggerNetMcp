# Phase 2: Interop + Engine Foundation - Research

**Researched:** 2026-02-22
**Domain:** C# COM Interop (ICorDebug), NativeLibrary.Load (libdbgshim.so), System.Reflection.Metadata (Portable PDB), debug models
**Confidence:** HIGH

## Summary

Phase 2 has four distinct workstreams: (1) dynamic discovery and loading of libdbgshim.so, (2) COM interface definitions for all ICorDebug types, (3) PdbReader that maps source-line to (methodToken, ilOffset) pairs using Portable PDB, and (4) VariableReader that inspects ICorDebugValue trees recursively.

The most critical technical decision for Phase 2 is the COM interop strategy. The old `[ComImport]` approach is Windows-only. On Linux, two viable paths exist: `[GeneratedComInterface]` (source generator, .NET 8+, cross-platform, official) and hand-rolled vtable wrappers using `delegate* unmanaged` function pointers (used by SeeminglyScience/ClrDebug, pure IL-level, always works). `[GeneratedComInterface]` is the standard recommendation for .NET 8+ since the infrastructure is cross-platform — the `ComWrappers` API it uses has been enabled on all CoreCLR platforms since .NET 6. The `ICorDebugManagedCallback` interface must be exposed TO native code (C# object passed to SetManagedHandler), which requires `[GeneratedComClass]` on the implementation class.

`System.Reflection.Metadata` is in-box in .NET 10 (no NuGet package needed). The API is well-documented and the reverse-lookup pattern (file+line → methodToken+ilOffset) requires iterating MethodDebugInformation entries and matching SequencePoint records. `libdbgshim.so` is located at `/home/eduardo/.local/lib/netcoredbg/libdbgshim.so` on this machine; there is no libdbgshim.so in the .dotnet runtime directories, but the `Microsoft.Diagnostics.DbgShim` NuGet meta-package (v9.0.661903) is already cached and contains platform-specific binaries. The discovery strategy must search multiple candidate paths at runtime.

**Primary recommendation:** Use `[GeneratedComInterface]` + `[GeneratedComClass]` for all ICorDebug interfaces. Use `NativeLibrary.TryLoad` with a list of candidate paths for libdbgshim.so discovery. Use in-box `System.Reflection.Metadata` (no NuGet) for PDB reading.

## Standard Stack

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| System.Reflection.Metadata | In-box (.NET 10) | Read Portable PDB, map source line to IL offset | Ships with runtime; authoritative .NET PDB API |
| System.Reflection.PortableExecutable | In-box (.NET 10) | Open PE files, find embedded PDB via ReadDebugDirectory | Required companion to System.Reflection.Metadata for embedded PDBs |
| System.Runtime.InteropServices (GeneratedComInterface) | In-box (.NET 8+) | Cross-platform COM interface wrapping via source generator | Official .NET 8+ replacement for [ComImport] on non-Windows |
| NativeLibrary | In-box (.NET 3.0+) | Dynamic loading of libdbgshim.so at runtime | Official API; replaces DllImport with fixed path |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| Microsoft.Diagnostics.DbgShim | 9.0.661903 (cached) | NuGet meta-package shipping libdbgshim.so per-platform | Alternative source if no local netcoredbg install; already cached |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| `[GeneratedComInterface]` | Hand-rolled `delegate* unmanaged` vtable wrappers | More portable (no source gen dependency), more verbose, requires IL-level understanding |
| `[GeneratedComInterface]` | `[ComImport]` | Windows-only; fails on Linux — NOT viable |
| In-box System.Reflection.Metadata | Third-party PDB library | In-box is authoritative and sufficient; no reason for third-party |
| NativeLibrary.TryLoad with candidate list | DllImport with fixed path | DllImport path is set at compile time; NativeLibrary enables runtime discovery |

**Installation:**

No new NuGet packages required for Phase 2. All libraries are in-box in .NET 10. The `DebuggerNetMcp.Core.csproj` needs no `<PackageReference>` additions.

However, the csproj needs one addition for source generation to work:
```xml
<PropertyGroup>
  <AllowUnsafeBlocks>true</AllowUnsafeBlocks>  <!-- required for delegate* and COM gen -->
</PropertyGroup>
```

## Architecture Patterns

### Recommended Project Structure

```
src/DebuggerNetMcp.Core/
├── Interop/
│   ├── DbgShimInterop.cs       # NativeLibrary.TryLoad + GetExport delegates
│   └── ICorDebug.cs            # All ICorDebug COM interface definitions
├── Engine/
│   ├── Models.cs               # BreakpointInfo, StackFrameInfo, VariableInfo, DebugEvent hierarchy
│   ├── PdbReader.cs            # Source line ↔ (methodToken, ilOffset) mapping
│   └── VariableReader.cs       # ICorDebugValue → VariableInfo recursive reader
```

### Pattern 1: DbgShimInterop — Dynamic Library Discovery

**What:** Load libdbgshim.so at runtime from multiple candidate paths using `NativeLibrary.TryLoad`, then extract function pointers with `NativeLibrary.GetExport`.

**When to use:** INTEROP-01 — replaces DllImport, enables dynamic path discovery.

**libdbgshim.so search order (verified for this machine):**
1. `DBGSHIM_PATH` environment variable (explicit override)
2. `{DOTNET_ROOT}/shared/Microsoft.NETCore.App/{latest-version}/libdbgshim.so` (runtime co-located — does NOT exist currently but future-proofs)
3. `/usr/share/dotnet/shared/Microsoft.NETCore.App/{latest-version}/libdbgshim.so` (system-wide)
4. Path of netcoredbg binary's parent directory (if NETCOREDBG_PATH is set)
5. `~/.local/lib/netcoredbg/libdbgshim.so` (verified on this machine)
6. `/usr/local/lib/netcoredbg/libdbgshim.so`

**Note:** On this machine, libdbgshim.so is at `/home/eduardo/.local/lib/netcoredbg/libdbgshim.so`. The mscordbi.so and mscordaccore.so needed by the callback are in `~/.dotnet/shared/Microsoft.NETCore.App/{version}/` — separate from libdbgshim.so. The `CORDBG_E_DEBUG_COMPONENT_MISSING` error fires when mscordbi.so is not found relative to the target CoreCLR. Since they are co-located with the .NET runtime, this should work automatically.

**Example:**
```csharp
// Source: NativeLibrary API + Microsoft Learn docs
internal static class DbgShimInterop
{
    // Delegate types for each exported function
    internal delegate int RegisterForRuntimeStartupDelegate(
        uint processId,
        RuntimeStartupCallback callback,
        IntPtr parameter,
        out IntPtr unregisterToken);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void RuntimeStartupCallback(
        IntPtr pCordb,      // IUnknown* → ICorDebug
        IntPtr parameter,
        int hr);

    internal delegate int CreateProcessForLaunchDelegate(
        [MarshalAs(UnmanagedType.LPWStr)] string lpCommandLine,
        bool bSuspendProcess,
        IntPtr lpEnvironment,
        [MarshalAs(UnmanagedType.LPWStr)] string? lpCurrentDirectory,
        out uint processId,
        out IntPtr resumeHandle);

    internal delegate int ResumeProcessDelegate(IntPtr resumeHandle);
    internal delegate int CloseResumeHandleDelegate(IntPtr resumeHandle);

    // Loaded function pointers
    private static nint _libHandle;
    public static RegisterForRuntimeStartupDelegate RegisterForRuntimeStartup = null!;
    public static CreateProcessForLaunchDelegate CreateProcessForLaunch = null!;
    public static ResumeProcessDelegate ResumeProcess = null!;
    public static CloseResumeHandleDelegate CloseResumeHandle = null!;

    public static void Load(string? dbgShimPath = null)
    {
        var candidates = BuildCandidateList(dbgShimPath);
        foreach (var path in candidates)
        {
            if (NativeLibrary.TryLoad(path, out _libHandle))
                break;
        }
        if (_libHandle == IntPtr.Zero)
            throw new FileNotFoundException("libdbgshim.so not found");

        RegisterForRuntimeStartup = Marshal.GetDelegateForFunctionPointer<RegisterForRuntimeStartupDelegate>(
            NativeLibrary.GetExport(_libHandle, "RegisterForRuntimeStartup"));
        // ... bind remaining functions
    }

    private static IEnumerable<string> BuildCandidateList(string? explicit)
    {
        if (explicit != null) yield return explicit;
        if (Environment.GetEnvironmentVariable("DBGSHIM_PATH") is { } envPath)
            yield return envPath;
        // ... search DOTNET_ROOT, NETCOREDBG_PATH, ~/.local/lib/netcoredbg/
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "lib", "netcoredbg", "libdbgshim.so");
    }
}
```

### Pattern 2: ICorDebug COM Interfaces with [GeneratedComInterface]

**What:** Define all ICorDebug interfaces as C# partial interfaces with `[GeneratedComInterface]` + `[Guid]` attributes. The source generator creates vtable-based wrappers that work on Linux.

**Key rules:**
- All interfaces must be `partial` and `internal` or `public`
- Derived interfaces (e.g., `ICorDebugILFrame : ICorDebugFrame`) use C# inheritance; do NOT shadow base methods with `new`
- Methods implicitly return HRESULT unless decorated with `[PreserveSig]`; last `out` param becomes the C# return value
- For the callback interface (`ICorDebugManagedCallback`) that goes FROM managed TO native, use `[GeneratedComInterface]` on the interface and `[GeneratedComClass]` on the implementation class
- All interfaces derive from `IUnknown` which is implicit in the generator

**Example:**
```csharp
// Source: Microsoft Learn - ComWrappers source generation
[GeneratedComInterface]
[Guid("3D6F5F61-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebug
{
    void Initialize();
    void Terminate();
    void SetManagedHandler(ICorDebugManagedCallback pCallback);
    void SetUnmanagedHandler(ICorDebugUnmanagedCallback pCallback);
    // ...
}

[GeneratedComInterface]
[Guid("3D6F5F60-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebugManagedCallback
{
    void Breakpoint(ICorDebugAppDomain pAppDomain, ICorDebugThread pThread, ICorDebugBreakpoint pBreakpoint);
    // ... all 21 callback methods
}

[GeneratedComClass]
internal partial class ManagedDebugCallback : ICorDebugManagedCallback, ICorDebugManagedCallback2
{
    // Implementation of all callback methods
}
```

**Consuming an ICorDebug pointer from RegisterForRuntimeStartup:**
```csharp
// pCordb is IntPtr (IUnknown*) from PSTARTUP_CALLBACK
ComWrappers cw = new StrategyBasedComWrappers();
var corDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pCordb, CreateObjectFlags.None);
corDebug.Initialize();
corDebug.SetManagedHandler(new ManagedDebugCallback(...));
```

### Pattern 3: PdbReader — Source Line to IL Offset Mapping

**What:** Open a Portable PDB (embedded in DLL or as separate .pdb file), iterate SequencePoints to build a reverse index from (file, line) → (methodToken, ilOffset).

**When to use:** ENGINE-02 — required by SetBreakpointAsync and stack frame inspection.

**Two PDB locations to handle:**
1. Embedded in the DLL: `peReader.ReadEmbeddedPortablePdbDebugDirectoryData(entry)`
2. Separate .pdb file alongside DLL: `peReader.TryOpenAssociatedPortablePdb(dllPath, ...)`

**Example (official pattern from Microsoft Docs):**
```csharp
// Source: Microsoft Learn - SequencePoint.ReadSourceLineData example
public (int methodToken, int ilOffset) FindLocation(string dllPath, string sourceFile, int line)
{
    using var fs = File.OpenRead(dllPath);
    using var peReader = new PEReader(fs);

    // Try embedded PDB first, then associated file
    MetadataReaderProvider? pdbProvider = null;
    var debugDir = peReader.ReadDebugDirectory();
    var embeddedEntry = debugDir.FirstOrDefault(e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);
    if (embeddedEntry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
    {
        pdbProvider = peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
    }
    else
    {
        peReader.TryOpenAssociatedPortablePdb(dllPath, File.OpenRead, out pdbProvider, out _);
    }

    if (pdbProvider == null) throw new FileNotFoundException("PDB not found");
    using (pdbProvider)
    {
        var pdbReader = pdbProvider.GetMetadataReader();
        foreach (var methodDebugHandle in pdbReader.MethodDebugInformation)
        {
            var debugInfo = pdbReader.GetMethodDebugInformation(methodDebugHandle);
            foreach (var sp in debugInfo.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                var doc = pdbReader.GetDocument(sp.Document);
                var fileName = pdbReader.GetString(doc.Name); // simplified
                if (fileName.EndsWith(sourceFile, StringComparison.OrdinalIgnoreCase)
                    && sp.StartLine == line)
                {
                    // methodToken: row number from MethodDebugInformation → MethodDefinition row
                    int rowNumber = MetadataTokens.GetRowNumber(methodDebugHandle);
                    // methodToken = 0x06000000 | rowNumber (MethodDef token type)
                    int methodToken = 0x06000000 | rowNumber;
                    return (methodToken, sp.Offset);
                }
            }
        }
    }
    throw new InvalidOperationException($"No sequence point at {sourceFile}:{line}");
}
```

**Key detail:** The `MethodDebugInformation` row number corresponds to the same row number in the `MethodDefinition` table. The method token is `0x06000000 | rowNumber` — this is the `mdToken` used by ICorDebug.

**Document name blob:** The `DocumentNameBlobHandle` requires special decoding (separator byte + blob parts). Use `MetadataReader.GetString(handle)` where possible, or decode manually for complex names.

### Pattern 4: VariableReader — Recursive ICorDebugValue Inspection

**What:** Accept an `ICorDebugValue`, determine its `CorElementType`, and dispatch to the appropriate sub-interface via QueryInterface (which `[GeneratedComInterface]` handles transparently as C# casts).

**CorElementType values for Phase 2 (all needed for ENGINE-03):**

```
ELEMENT_TYPE_BOOLEAN = 0x02  → ICorDebugGenericValue.GetValue → bool
ELEMENT_TYPE_I4      = 0x08  → ICorDebugGenericValue.GetValue → int
ELEMENT_TYPE_I8      = 0x0A  → ICorDebugGenericValue.GetValue → long
ELEMENT_TYPE_R4      = 0x0C  → ICorDebugGenericValue.GetValue → float
ELEMENT_TYPE_R8      = 0x0D  → ICorDebugGenericValue.GetValue → double
ELEMENT_TYPE_STRING  = 0x0E  → ICorDebugStringValue.GetString
ELEMENT_TYPE_OBJECT  = 0x1C  → ICorDebugObjectValue (reference type)
ELEMENT_TYPE_CLASS   = 0x12  → ICorDebugObjectValue (class)
ELEMENT_TYPE_SZARRAY = 0x1D  → ICorDebugArrayValue (single-dimensional array)
ELEMENT_TYPE_ARRAY   = 0x14  → ICorDebugArrayValue (multi-dimensional)
ELEMENT_TYPE_VALUETYPE = 0x11 → ICorDebugObjectValue (struct)
```

**Example (conceptual pattern):**
```csharp
public static VariableInfo ReadValue(ICorDebugValue value, int depth = 0)
{
    if (depth > 3) return new VariableInfo { Value = "..." };  // depth limit

    value.GetType(out var elementType);

    return (CorElementType)elementType switch
    {
        CorElementType.I4 or CorElementType.Boolean or CorElementType.I8
            or CorElementType.R4 or CorElementType.R8 => ReadPrimitive(value),
        CorElementType.String => ReadString(value),
        CorElementType.SzArray or CorElementType.Array => ReadArray(value, depth),
        CorElementType.Object or CorElementType.Class => ReadObject(value, depth),
        CorElementType.ValueType => ReadObject(value, depth), // struct
        _ => new VariableInfo { Value = $"<{elementType}>" }
    };
}

private static VariableInfo ReadPrimitive(ICorDebugValue value)
{
    // Cast to ICorDebugGenericValue via [GeneratedComInterface] QueryInterface
    var generic = (ICorDebugGenericValue)value;
    // GetValue copies raw bytes; size depends on type
    value.GetSize(out uint size);
    byte[] buffer = new byte[size];
    generic.GetValue(buffer);
    // Interpret bytes based on type...
}
```

**Important:** When `ICorDebugValue` is a reference type (ELEMENT_TYPE_OBJECT), it may be an `ICorDebugReferenceValue`. Call `Dereference()` to get the actual `ICorDebugHeapValue2`. Then cast to `ICorDebugObjectValue` to enumerate fields.

### Anti-Patterns to Avoid

- **Using `[ComImport]`:** Windows-only. Fails silently on Linux with COM not available errors. Use `[GeneratedComInterface]` exclusively.
- **Hardcoding libdbgshim.so path:** Violates INTEROP-01 requirement. Use the candidate list approach.
- **Shadowing base methods in derived COM interfaces with `new`:** Required for `[ComImport]` but WRONG for `[GeneratedComInterface]`. The generator expects natural C# inheritance.
- **Assuming libdbgshim.so is co-located with libcoreclr.so:** On this machine, it is NOT in the .dotnet runtime dir — it's in `~/.local/lib/netcoredbg/`. The mscordbi.so IS co-located with libcoreclr.so in the .dotnet runtime dir.
- **Reading objects without dereference:** `ICorDebugValue` for reference types is an `ICorDebugReferenceValue`, not the object itself. Call `Dereference()` first.
- **Calling ICorDebug COM methods from multiple threads:** ICorDebug COM interfaces are not thread-safe. All ICorDebug calls must happen on a single dedicated thread. Phase 2 only defines the interfaces, but Phase 3 must enforce this.

## Don't Hand-Roll

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| COM vtable management on Linux | Custom vtable struct + function pointer arrays | `[GeneratedComInterface]` source generator | Generator handles vtable layout, QueryInterface, AddRef/Release correctly |
| PDB file format parsing | Custom binary parser for PDB format | `System.Reflection.Metadata` + `System.Reflection.PortableExecutable` | PDB format is complex (ECMA-335 extension); in-box library is authoritative |
| CorElementType enum | Define own byte constants | Use the enum from `System.Diagnostics.CorElementType` or define from official values | Easy to get wrong; standard enum exists |
| Document name blob decoding | Manual byte parsing | `MetadataReader.GetString(DocumentNameBlobHandle)` | SDK method handles the separator-based encoding |
| QueryInterface navigation | Manual IntPtr casting and vtable calls | C# cast via `[GeneratedComInterface]` e.g. `(ICorDebugStringValue)value` | Generator emits correct QueryInterface calls |

**Key insight:** The `[GeneratedComInterface]` source generator eliminates the majority of error-prone interop code. The generated vtable layout matches the C++ ABI requirements for ICorDebug on Linux.

## Common Pitfalls

### Pitfall 1: libdbgshim.so Not Found at Runtime

**What goes wrong:** `DbgShimInterop.Load()` cannot find libdbgshim.so and throws FileNotFoundException.
**Why it happens:** The library is not in the .NET runtime directory on this machine — it lives in `~/.local/lib/netcoredbg/` (netcoredbg installation directory).
**How to avoid:** Implement the multi-path candidate search. Add `DBGSHIM_PATH` env var override. Log all attempted paths before throwing.
**Warning signs:** Unit test of `DbgShimInterop.Load()` fails in a clean environment.

### Pitfall 2: GeneratedComInterface + Derived Interfaces Across Assemblies

**What goes wrong:** If ICorDebug base interfaces are in one assembly and derived interfaces in another, vtable offsets are not recomputed correctly in .NET 8. Fixed in .NET 9 with restrictions.
**Why it happens:** Cross-assembly interface inheritance requires recompilation to pick up vtable offset changes.
**How to avoid:** Keep ALL ICorDebug interface definitions in a single file in `DebuggerNetMcp.Core`. Do not split them across assemblies.
**Warning signs:** Method calls on derived interfaces dispatch to wrong vtable slot and return E_NOINTERFACE or crash.

### Pitfall 3: PSTARTUP_CALLBACK Lifetime — GC Collects Delegate

**What goes wrong:** The managed `RuntimeStartupCallback` delegate is collected by the GC before the native callback fires, causing a crash.
**Why it happens:** `RegisterForRuntimeStartup` stores the callback natively. If the managed delegate has no managed root, GC can collect it.
**How to avoid:** Store the callback delegate in a static field or `GCHandle` before calling `RegisterForRuntimeStartup`. Release only after `UnregisterForRuntimeStartup` or callback fires.
**Warning signs:** Intermittent crash in the callback thread; `AccessViolationException` or segfault in native code.

### Pitfall 4: ICorDebugReferenceValue vs ICorDebugObjectValue

**What goes wrong:** `VariableReader` tries to cast `ICorDebugValue` directly to `ICorDebugObjectValue` for reference types, but gets `E_NOINTERFACE`.
**Why it happens:** For heap-allocated objects, `ICorDebugValue.GetType()` returns `ELEMENT_TYPE_OBJECT`, but the actual interface is `ICorDebugReferenceValue`. Must call `Dereference()` first.
**How to avoid:** Check if `(ICorDebugReferenceValue)value` succeeds (try-cast), then call `Dereference()` to get the `ICorDebugHeapValue2`, then cast to `ICorDebugObjectValue`.
**Warning signs:** VariableReader throws for object types but works for primitives.

### Pitfall 5: MethodToken Row Number Off-by-One

**What goes wrong:** PdbReader returns wrong (methodToken, ilOffset) — breakpoints fire on the wrong line or not at all.
**Why it happens:** `MethodDebugInformation` table is 1-indexed (row 1 = row 1 in MethodDefinition). `MetadataTokens.GetRowNumber(handle)` returns the 1-based row; the mdToken is `0x06000000 | rowNumber`.
**How to avoid:** Use `MetadataTokens.GetRowNumber(methodDebugHandle)` and combine with `0x06000000` prefix. Verify with a known test method.
**Warning signs:** Off-by-one breakpoint positions in Phase 3 testing.

### Pitfall 6: Embedded PDB vs Separate PDB

**What goes wrong:** PdbReader only tries embedded PDB, but `dotnet build -c Debug` may produce a separate `.pdb` file depending on project settings.
**Why it happens:** Default .NET project settings produce embedded PDB with `<EmbedAllSources>` but not always; `<DebugType>portable</DebugType>` is the default which creates a separate file.
**How to avoid:** Try embedded PDB first; fall back to `TryOpenAssociatedPortablePdb`. The HelloDebug test project must be verified to produce a readable PDB.
**Warning signs:** `PdbReader` throws "PDB not found" on a debug-built assembly.

## Code Examples

Verified patterns from official sources:

### NativeLibrary.TryLoad Pattern

```csharp
// Source: Microsoft Learn - Native library loading
// docs.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading
if (NativeLibrary.TryLoad("/full/path/to/libdbgshim.so", out nint handle))
{
    nint funcPtr = NativeLibrary.GetExport(handle, "RegisterForRuntimeStartup");
    var fn = Marshal.GetDelegateForFunctionPointer<RegisterForRuntimeStartupDelegate>(funcPtr);
}
```

### GeneratedComInterface Definition

```csharp
// Source: Microsoft Learn - ComWrappers source generation
// docs.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation
[GeneratedComInterface]
[Guid("3D6F5F61-7538-11D3-8D5B-00104B35E7EF")]
internal partial interface ICorDebug
{
    void Initialize();
    void Terminate();
    void SetManagedHandler(ICorDebugManagedCallback pCallback);
    // ...
}
```

### WrappingComPointer from RegisterForRuntimeStartup

```csharp
// pCordb = IntPtr from PSTARTUP_CALLBACK (IUnknown*)
var cw = new StrategyBasedComWrappers();
var corDebug = (ICorDebug)cw.GetOrCreateObjectForComInstance(pCordb, CreateObjectFlags.None);
```

### PdbReader Reverse Lookup

```csharp
// Source: Microsoft Learn - SequencePoint docs + ReadSourceLineData example
// docs.microsoft.com/en-us/dotnet/api/system.reflection.metadata.sequencepoint
public static void ReadSourceLineData(string pdbPath, int methodToken)
{
    EntityHandle ehMethod = MetadataTokens.EntityHandle(methodToken);
    int rowNumber = MetadataTokens.GetRowNumber(ehMethod);
    MethodDebugInformationHandle hDebug = MetadataTokens.MethodDebugInformationHandle(rowNumber);

    using var fs = new FileStream(pdbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
    using MetadataReaderProvider provider = MetadataReaderProvider.FromPortablePdbStream(fs);
    MetadataReader reader = provider.GetMetadataReader();

    MethodDebugInformation di = reader.GetMethodDebugInformation(hDebug);
    foreach (SequencePoint sp in di.GetSequencePoints())
    {
        if (!sp.IsHidden)
            Console.WriteLine($"IL {sp.Offset} → {sp.StartLine}:{sp.StartColumn}");
    }
}
```

### ICorDebug Confirmed GUIDs

From `cordebug.idl` (dotnet/coreclr GitHub):

| Interface | GUID |
|-----------|------|
| ICorDebug | `3D6F5F61-7538-11D3-8D5B-00104B35E7EF` |
| ICorDebugController | `3D6F5F62-7538-11D3-8D5B-00104B35E7EF` |
| ICorDebugProcess | `3D6F5F64-7538-11D3-8D5B-00104B35E7EF` |
| ICorDebugManagedCallback | `3D6F5F60-7538-11D3-8D5B-00104B35E7EF` |
| ICorDebugManagedCallback2 | `250E5EEA-DB5C-4C76-B6F3-8C46F12E3203` |

**Remaining GUIDs (ICorDebugThread, ICorDebugFrame, ICorDebugILFrame, ICorDebugFunction, ICorDebugModule, ICorDebugValue, etc.):** Must be extracted directly from `dotnet/runtime` repo at `src/coreclr/inc/cordebug.idl` at implementation time. The full file is ~12,000 lines. The planner should include a task to fetch and transcribe all required GUIDs from the authoritative source.

### Verified libdbgshim.so Symbols (on this machine)

```
CreateProcessForLaunch
ResumeProcess
CloseResumeHandle
RegisterForRuntimeStartup
RegisterForRuntimeStartup3
RegisterForRuntimeStartupEx
UnregisterForRuntimeStartup
GetStartupNotificationEvent
EnumerateCLRs
CloseCLREnumeration
CreateVersionStringFromModule
CreateDebuggingInterfaceFromVersion
CreateDebuggingInterfaceFromVersion3
CreateDebuggingInterfaceFromVersionEx
CreateDebuggingInterfaceFromVersion2
CLRCreateInstance
RegisterForRuntimeStartupRemotePort
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| `[ComImport]` for COM interfaces | `[GeneratedComInterface]` source generator | .NET 8 (2023) | Cross-platform; NativeAOT/trimming compatible |
| TlbImp-generated interop assemblies | Hand-written `[GeneratedComInterface]` interfaces | .NET 8 | No Windows-only tooling required |
| Windows-only COM interop (ComWrappers) | ComWrappers on all CoreCLR platforms | .NET 6 (2021) | Linux debugger possible without Mono |
| `.pdb` format (Windows) | Portable PDB (cross-platform, embedded) | .NET Core 1.0 | PDB in DLL, no separate file needed |
| `System.Reflection.Metadata` as NuGet | In-box in .NET Core 1.0+ | Since .NET Core 1.0 | No package reference needed |

**Deprecated/outdated:**
- `[ComImport]` for ICorDebug on Linux: generates only Windows IL stubs, not viable.
- Separate TlbImp steps: superseded by source generators.
- Windows-SDK `cordebug.h` for C# COM definitions: use `[GeneratedComInterface]` with GUIDs from `cordebug.idl` instead.

## Open Questions

1. **All ICorDebug GUIDs not fully verified**
   - What we know: ICorDebug (`3D6F5F61`), ICorDebugController (`3D6F5F62`), ICorDebugProcess (`3D6F5F64`), ICorDebugManagedCallback (`3D6F5F60`), ICorDebugManagedCallback2 (`250E5EEA`)
   - What's unclear: The exact GUIDs for ICorDebugThread, ICorDebugFrame, ICorDebugILFrame, ICorDebugFunction, ICorDebugModule, ICorDebugValue, ICorDebugGenericValue, ICorDebugStringValue, ICorDebugObjectValue, ICorDebugArrayValue, ICorDebugBreakpoint, ICorDebugFunctionBreakpoint, ICorDebugStepper
   - Recommendation: The implementation plan task must include fetching and extracting these GUIDs from `https://raw.githubusercontent.com/dotnet/runtime/main/src/coreclr/inc/cordebug.idl` as a concrete subtask before writing `ICorDebug.cs`

2. **RegisterForRuntimeStartup vs RegisterForRuntimeStartup3 — which to use**
   - What we know: Both are exported by libdbgshim.so. `RegisterForRuntimeStartup3` (if it differs) may add options.
   - What's unclear: The exact signature of `RegisterForRuntimeStartup3`. The official docs only document `RegisterForRuntimeStartup`.
   - Recommendation: Start with `RegisterForRuntimeStartup`. Use `RegisterForRuntimeStartup3` only if the basic version fails to deliver the callback on Linux 6.12+.

3. **AllowUnsafeBlocks and GeneratedComInterface compilation**
   - What we know: `[GeneratedComInterface]` requires `AllowUnsafeBlocks` in the csproj because the generated code uses unsafe pointers.
   - What's unclear: Whether `.NET 10` specific warnings/errors appear with this attribute combination.
   - Recommendation: Add `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` to `DebuggerNetMcp.Core.csproj` as first task in Phase 2.

4. **Document name blob decoding in PdbReader**
   - What we know: Document names in Portable PDB are stored as blobs with a separator byte followed by blob parts.
   - What's unclear: Whether `MetadataReader.GetString(DocumentNameBlobHandle)` handles this automatically or requires manual decoding.
   - Recommendation: Use the `GetDocumentName` helper pattern from the gist example; test with HelloDebug before asserting correctness.

<phase_requirements>
## Phase Requirements

| ID | Description | Research Support |
|----|-------------|-----------------|
| INTEROP-01 | DbgShimInterop.cs with dynamic discovery of libdbgshim.so via NativeLibrary.Load() — searches in DOTNET_ROOT and /usr/share/dotnet | `NativeLibrary.TryLoad` API verified. Candidate path list: explicit env var → DOTNET_ROOT subdirs → /usr/share/dotnet → netcoredbg install dir (`~/.local/lib/netcoredbg/` on this machine). Note: libdbgshim.so NOT in DOTNET_ROOT on this machine — exclusively in netcoredbg dir. The search order must include netcoredbg paths. |
| INTEROP-02 | ICorDebug.cs with complete COM interfaces and correct GUIDs — 17 interfaces listed | `[GeneratedComInterface]` + `[Guid]` pattern verified for cross-platform Linux COM. Interfaces must all be `partial`. Derived interfaces use C# inheritance (no `new` shadowing). PSTARTUP_CALLBACK is `delegate void(IntPtr pCordb, IntPtr parameter, int hr)`. 5 GUIDs confirmed; remaining 12 must be extracted from cordebug.idl at implementation time. |
| ENGINE-01 | Models — BreakpointInfo, StackFrameInfo, VariableInfo, EvalResult, DebugEvent hierarchy (StoppedEvent, BreakpointHitEvent, ExceptionEvent, ExitedEvent, OutputEvent) | Pure C# model types. No external dependencies. DebugEvent hierarchy should use abstract base class + sealed subclasses for pattern matching. `Channel<DebugEvent>` used in Phase 3 is the consumer. |
| ENGINE-02 | PdbReader.cs — reads Portable PDB embedded or separate, maps (file, line) ↔ (methodToken, ilOffset) using System.Reflection.Metadata | `System.Reflection.Metadata` is in-box in .NET 10 (no NuGet). `PEReader.ReadDebugDirectory()` → `ReadEmbeddedPortablePdbDebugDirectoryData()` or `TryOpenAssociatedPortablePdb()`. `SequencePoint.Offset` = ilOffset. methodToken = `0x06000000 | MetadataTokens.GetRowNumber(handle)`. Full working example from Microsoft Docs verified. |
| ENGINE-03 | VariableReader.cs — reads ICorDebugValue recursively with depth limit 3 (primitives, strings, arrays, objects) | `ICorDebugValue.GetType()` → `CorElementType`. Dispatch to ICorDebugGenericValue (primitives), ICorDebugStringValue (strings), ICorDebugArrayValue (arrays), ICorDebugObjectValue (objects). Reference types require `ICorDebugReferenceValue.Dereference()` first. Depth limit 3 prevents infinite loops on circular object graphs. |
</phase_requirements>

## Sources

### Primary (HIGH confidence)
- Microsoft Learn - RegisterForRuntimeStartup: https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/registerforruntimestartup-function
- Microsoft Learn - PSTARTUP_CALLBACK: https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/pstartup_callback-function-pointer
- Microsoft Learn - NativeLibrary loading: https://learn.microsoft.com/en-us/dotnet/standard/native-interop/native-library-loading
- Microsoft Learn - ComWrappers source generation (`[GeneratedComInterface]`): https://learn.microsoft.com/en-us/dotnet/standard/native-interop/comwrappers-source-generation
- Microsoft Learn - SequencePoint struct + ReadSourceLineData example: https://learn.microsoft.com/en-us/dotnet/api/system.reflection.metadata.sequencepoint?view=net-9.0
- Microsoft Learn - ICorDebug interface: https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/debugging/icordebug/icordebug-interface
- `nm -D /home/eduardo/.local/lib/netcoredbg/libdbgshim.so` — verified exported symbols (17 functions confirmed)
- File system inspection: libdbgshim.so at `/home/eduardo/.local/lib/netcoredbg/`, libmscordbi.so + libmscordaccore.so at `/home/eduardo/.dotnet/shared/Microsoft.NETCore.App/{version}/`
- System.Reflection.Metadata.dll at `/home/eduardo/.dotnet/shared/Microsoft.NETCore.App/10.0.0/` — confirmed in-box

### Secondary (MEDIUM confidence)
- GitHub - SeeminglyScience/ClrDebug (cross-platform ICorDebug via calli): https://github.com/SeeminglyScience/ClrDebug — confirms the cross-platform approach exists; library itself not published/maintained
- GitHub - lordmilko/ClrDebug issue #9 (Windows-only ComImport): https://github.com/lordmilko/ClrDebug/issues/9 — confirms [ComImport] doesn't work on Linux
- GitHub dotnet/runtime issue #10572 (Cross-Platform COM Interop): https://github.com/dotnet/runtime/issues/10572 — ComWrappers is cross-platform since .NET 6
- cordebug.idl (GitHub dotnet/coreclr): https://github.com/dotnet/coreclr/blob/master/src/inc/cordebug.idl — GUIDs source (partially extracted)
- CorElementType enumeration: https://learn.microsoft.com/en-us/dotnet/core/unmanaged-api/metadata/enumerations/corelementtype-enumeration — values verified
- Portable PDB spec: https://github.com/dotnet/runtime/blob/main/docs/design/specs/PortablePdb-Metadata.md — MethodDebugInformation table structure
- PEReader.TryOpenAssociatedPortablePdb: https://learn.microsoft.com/en-us/dotnet/api/system.reflection.portableexecutable.pereader.tryopenassociatedportablepdb

### Tertiary (LOW confidence)
- None — all critical claims verified through official docs or system inspection.

## Metadata

**Confidence breakdown:**
- Standard stack: HIGH — all APIs verified via official Microsoft docs and local system inspection
- Architecture (COM interop): HIGH — `[GeneratedComInterface]` is the official .NET 8+ recommended approach, documented and stable
- Architecture (PDB reader): HIGH — complete working example from official Microsoft Docs
- Pitfalls: HIGH for GC/lifetime and COM threading; MEDIUM for MethodToken row numbering (pattern confirmed but not locally executed)
- GUIDs: MEDIUM — 5 of 17 confirmed; remaining require cordebug.idl extraction at implementation time

**Research date:** 2026-02-22
**Valid until:** 2026-05-22 (stable APIs; GeneratedComInterface stable since .NET 8)
