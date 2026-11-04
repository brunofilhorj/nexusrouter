namespace DropCopySim.Integration.Tests;

public class ScaffoldTests
{
    [Fact]
    public void Integration_Project_IsWired()
    {
        Assert.True(typeof(DropCopySim.Core.Generation.SimulationState).IsEnum);
    }
}
