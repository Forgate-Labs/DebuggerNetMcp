namespace DebuggerNetMcp.Tests;

public class MathTests
{
    // BP-DTEST: Set breakpoint on the line with "int result = a + b;" to test DTEST-01/02
    [Fact]
    public void AddTwoNumbers_ReturnsCorrectSum()
    {
        int a = 21;
        int b = 21;
        int result = a + b; // BP-DTEST
        string label = "sum";
        Assert.Equal(42, result);
    }

    [Fact]
    public void MultiplyNumbers_ReturnsProduct()
    {
        int x = 6;
        int y = 7;
        int product = x * y; // BP-DTEST-2
        Assert.Equal(42, product);
    }
}
