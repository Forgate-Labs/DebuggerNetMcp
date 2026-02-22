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

internal static class DbgShimInterop
{
    public static RegisterForRuntimeStartupDelegate RegisterForRuntimeStartup = null!;
    public static CreateProcessForLaunchDelegate CreateProcessForLaunch = null!;
    public static ResumeProcessDelegate ResumeProcess = null!;
    public static CloseResumeHandleDelegate CloseResumeHandle = null!;

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

        CreateProcessForLaunch = Marshal.GetDelegateForFunctionPointer<CreateProcessForLaunchDelegate>(
            NativeLibrary.GetExport(_libHandle, "CreateProcessForLaunch"));

        ResumeProcess = Marshal.GetDelegateForFunctionPointer<ResumeProcessDelegate>(
            NativeLibrary.GetExport(_libHandle, "ResumeProcess"));

        CloseResumeHandle = Marshal.GetDelegateForFunctionPointer<CloseResumeHandleDelegate>(
            NativeLibrary.GetExport(_libHandle, "CloseResumeHandle"));
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
