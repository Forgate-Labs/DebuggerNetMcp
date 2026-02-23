using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DebuggerNetMcp.Core.Interop;

internal delegate int RegisterForRuntimeStartupDelegate(
    uint processId,
    RuntimeStartupCallback callback,
    IntPtr parameter,
    out IntPtr unregisterToken);

// RegisterForRuntimeStartup3: newer API that supports suspended-process launch.
// Real signature (confirmed from disasm of RegisterForRuntimeStartupEx thunk):
//   (dwProcessId, szApplicationGroupId, bSuspendProcess, pfnCallback, parameter, ppUnregisterToken)
// NOTE: appGroupId comes BEFORE bSuspendProcess.
internal delegate int RegisterForRuntimeStartup3Delegate(
    uint processId,
    IntPtr szApplicationGroupId,   // NULL on Linux; wchar_t* on macOS
    [MarshalAs(UnmanagedType.Bool)] bool bSuspendProcess,
    RuntimeStartupCallback callback,
    IntPtr parameter,
    out IntPtr unregisterToken);

[UnmanagedFunctionPointer(CallingConvention.StdCall)]
internal delegate void RuntimeStartupCallback(
    IntPtr pCordb,
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

// Attach-to-running-process APIs (not needed for launch via RegisterForRuntimeStartup)
internal delegate int EnumerateCLRsDelegate(
    uint processId,
    out IntPtr ppHandleArray,   // HANDLE** — array of CLR module handles
    out IntPtr ppStringArray,   // LPWSTR** — array of CLR module path strings
    out uint pdwArrayLength);

internal delegate int CloseCLREnumerationDelegate(
    IntPtr pHandleArray,
    IntPtr pStringArray,
    uint dwArrayLength);

internal delegate int CreateVersionStringFromModuleDelegate(
    uint pidDebuggee,
    [MarshalAs(UnmanagedType.LPWStr)] string szModuleName,
    IntPtr pBuffer,     // LPWSTR output buffer (caller-allocated)
    uint cchBuffer,
    out uint pdwLength);

internal delegate int CreateDebuggingInterfaceFromVersionExDelegate(
    int iDebuggerVersion,
    [MarshalAs(UnmanagedType.LPWStr)] string szDebuggeeVersion,
    out IntPtr ppCordb);

internal static class DbgShimInterop
{
    public static RegisterForRuntimeStartupDelegate RegisterForRuntimeStartup = null!;
    public static RegisterForRuntimeStartup3Delegate? RegisterForRuntimeStartup3 = null;
    public static CreateProcessForLaunchDelegate CreateProcessForLaunch = null!;
    public static ResumeProcessDelegate ResumeProcess = null!;
    public static CloseResumeHandleDelegate CloseResumeHandle = null!;
    public static EnumerateCLRsDelegate EnumerateCLRs = null!;
    public static CloseCLREnumerationDelegate CloseCLREnumeration = null!;
    public static CreateVersionStringFromModuleDelegate CreateVersionStringFromModule = null!;
    public static CreateDebuggingInterfaceFromVersionExDelegate CreateDebuggingInterfaceFromVersionEx = null!;

    private static nint _libHandle;

    // GC lifetime guard: keeps the RuntimeStartupCallback delegate alive until native code fires it
    private static RuntimeStartupCallback? _startupCallbackRef;

    /// <summary>
    /// Loads libdbgshim.so from the first found candidate path and binds all required function delegates.
    /// </summary>
    /// <param name="dbgShimPath">Optional explicit path to libdbgshim.so. Takes priority over all other candidates.</param>
    /// <exception cref="FileNotFoundException">Thrown if libdbgshim.so cannot be found in any candidate location.</exception>
    public static void Load(string? dbgShimPath = null)
    {
        var candidates = BuildCandidateList(dbgShimPath).ToList();

        foreach (var candidate in candidates)
        {
            if (NativeLibrary.TryLoad(candidate, out _libHandle))
                break;
        }

        if (_libHandle == IntPtr.Zero)
        {
            throw new FileNotFoundException(
                $"Could not load libdbgshim.so. Attempted paths:\n{string.Join("\n", candidates)}");
        }

        RegisterForRuntimeStartup = Marshal.GetDelegateForFunctionPointer<RegisterForRuntimeStartupDelegate>(
            NativeLibrary.GetExport(_libHandle, "RegisterForRuntimeStartup"));

        // Try to bind the v3 API (available in .NET 6+ era libdbgshim)
        if (NativeLibrary.TryGetExport(_libHandle, "RegisterForRuntimeStartup3", out IntPtr startup3Ptr))
        {
            RegisterForRuntimeStartup3 = Marshal.GetDelegateForFunctionPointer<RegisterForRuntimeStartup3Delegate>(startup3Ptr);
        }

        CreateProcessForLaunch = Marshal.GetDelegateForFunctionPointer<CreateProcessForLaunchDelegate>(
            NativeLibrary.GetExport(_libHandle, "CreateProcessForLaunch"));

        ResumeProcess = Marshal.GetDelegateForFunctionPointer<ResumeProcessDelegate>(
            NativeLibrary.GetExport(_libHandle, "ResumeProcess"));

        CloseResumeHandle = Marshal.GetDelegateForFunctionPointer<CloseResumeHandleDelegate>(
            NativeLibrary.GetExport(_libHandle, "CloseResumeHandle"));

        EnumerateCLRs = Marshal.GetDelegateForFunctionPointer<EnumerateCLRsDelegate>(
            NativeLibrary.GetExport(_libHandle, "EnumerateCLRs"));

        CloseCLREnumeration = Marshal.GetDelegateForFunctionPointer<CloseCLREnumerationDelegate>(
            NativeLibrary.GetExport(_libHandle, "CloseCLREnumeration"));

        CreateVersionStringFromModule = Marshal.GetDelegateForFunctionPointer<CreateVersionStringFromModuleDelegate>(
            NativeLibrary.GetExport(_libHandle, "CreateVersionStringFromModule"));

        CreateDebuggingInterfaceFromVersionEx = Marshal.GetDelegateForFunctionPointer<CreateDebuggingInterfaceFromVersionExDelegate>(
            NativeLibrary.GetExport(_libHandle, "CreateDebuggingInterfaceFromVersionEx"));
    }

    /// <summary>
    /// Keeps the RuntimeStartupCallback delegate alive in managed memory until after native code fires it.
    /// Phase 3 (DotnetDebugger.cs) MUST call this before RegisterForRuntimeStartup to prevent GC collection.
    /// </summary>
    public static void KeepAlive(RuntimeStartupCallback cb) => _startupCallbackRef = cb;

    private static IEnumerable<string> BuildCandidateList(string? explicitPath)
    {
        // 1. Explicit path parameter
        if (!string.IsNullOrEmpty(explicitPath))
            yield return explicitPath;

        // 2. DBGSHIM_PATH environment variable
        var dbgShimEnv = Environment.GetEnvironmentVariable("DBGSHIM_PATH");
        if (!string.IsNullOrEmpty(dbgShimEnv))
            yield return dbgShimEnv;

        // 3. DOTNET_ROOT/shared/Microsoft.NETCore.App subdirectories (newest first)
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "";
        if (!string.IsNullOrEmpty(dotnetRoot))
        {
            var dotnetRuntimeDir = Path.Combine(dotnetRoot, "shared", "Microsoft.NETCore.App");
            foreach (var path in EnumerateRuntimeLibDbgShim(dotnetRuntimeDir))
                yield return path;
        }

        // 4. /usr/share/dotnet/shared/Microsoft.NETCore.App subdirectories (newest first)
        foreach (var path in EnumerateRuntimeLibDbgShim("/usr/share/dotnet/shared/Microsoft.NETCore.App"))
            yield return path;

        // 5. NETCOREDBG_PATH environment variable directory
        var netcoredbgPath = Environment.GetEnvironmentVariable("NETCOREDBG_PATH");
        if (!string.IsNullOrEmpty(netcoredbgPath))
        {
            var dir = Path.GetDirectoryName(netcoredbgPath) ?? "";
            if (!string.IsNullOrEmpty(dir))
                yield return Path.Combine(dir, "libdbgshim.so");
        }

        // 6. ~/.local/lib/netcoredbg/libdbgshim.so
        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "lib", "netcoredbg", "libdbgshim.so");

        // 7. /usr/local/lib/netcoredbg/libdbgshim.so
        yield return "/usr/local/lib/netcoredbg/libdbgshim.so";
    }

    private static IEnumerable<string> EnumerateRuntimeLibDbgShim(string runtimeBaseDir)
    {
        if (!Directory.Exists(runtimeBaseDir))
            yield break;

        var subdirs = Directory.GetDirectories(runtimeBaseDir)
            .OrderByDescending(d => d)
            .ToList();

        foreach (var subdir in subdirs)
        {
            var candidate = Path.Combine(subdir, "libdbgshim.so");
            yield return candidate;
        }
    }
}
