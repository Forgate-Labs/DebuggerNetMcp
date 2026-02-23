using DebuggerNetMcp.Core.Engine;

namespace DebuggerNetMcp.Tests;

public sealed class DebuggerFixture : IAsyncLifetime
{
    public DotnetDebugger Debugger { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        var dbgShimPath = Environment.GetEnvironmentVariable("DBGSHIM_PATH")
            ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "bin", "libdbgshim.so");

        Debugger = new DotnetDebugger(dbgShimPath);
        await Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await Debugger.DisposeAsync();
    }
}

[CollectionDefinition("Debugger", DisableParallelization = true)]
public class DebuggerCollection : ICollectionFixture<DebuggerFixture> { }
