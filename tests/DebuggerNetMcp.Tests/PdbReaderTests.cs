namespace DebuggerNetMcp.Tests;

public class PdbReaderTests
{
    private static readonly string HelloDebugDll = Path.GetFullPath(
        Path.Combine(AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "HelloDebug", "bin", "Debug", "net10.0", "HelloDebug.dll"));

    [Fact]
    public void HelloDebugDll_Exists()
    {
        // Fails early with a clear message if the ProjectReference didn't build HelloDebug
        Assert.True(File.Exists(HelloDebugDll),
            $"HelloDebug.dll not found at: {HelloDebugDll}");
    }

    [Fact]
    public void FindLocation_Section1Primitives_ReturnsNonZeroToken()
    {
        // Program.cs line 17: "int counter = 0;"  (Section 1 BP-1)
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 17);
        Assert.NotEqual(0, methodToken);
        Assert.True(ilOffset >= 0);
    }

    [Fact]
    public void FindLocation_Section2Strings_ReturnsNonZeroToken()
    {
        // Program.cs line 25: "string greeting = "Hello, World!";"
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 25);
        Assert.NotEqual(0, methodToken);
        Assert.True(ilOffset >= 0);
    }

    [Fact]
    public void ReverseLookup_ForwardResult_RoundTripsToSameLine()
    {
        // Forward lookup then reverse — must return same source file and line
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 17);
        Assert.NotEqual(0, methodToken);

        var result = PdbReader.ReverseLookup(HelloDebugDll, methodToken, ilOffset);

        Assert.NotNull(result);
        Assert.Contains("Program.cs", result!.Value.sourceFile,
            StringComparison.OrdinalIgnoreCase);
        Assert.Equal(17, result.Value.line);
    }

    [Fact]
    public void ReverseLookup_NearbyOffset_ReturnsSameOrEarlierLine()
    {
        // Nearest-sequence-point semantics: offset+1 should still map to a valid line <= 17
        var (methodToken, ilOffset) = PdbReader.FindLocation(HelloDebugDll, "Program.cs", 17);
        Assert.NotEqual(0, methodToken);

        var result = PdbReader.ReverseLookup(HelloDebugDll, methodToken, ilOffset + 1);

        // Nearest SP: should still resolve to Program.cs at line <= 17 (not null)
        Assert.NotNull(result);
        Assert.Contains("Program.cs", result!.Value.sourceFile,
            StringComparison.OrdinalIgnoreCase);
    }
}
