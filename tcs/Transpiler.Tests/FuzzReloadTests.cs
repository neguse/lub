namespace TinyCs.Tests;

using TinyCs.Tests.SpecConformance;

// Generator for hot reload fuzzing. VmReloadTests in tcs2c.Tests applies the scenarios to tcs VM reload and checks the invariants
public class FuzzReloadTests
{
    [Fact]
    public void Generator_IsDeterministicPerSeed()
    {
        var first = new FuzzReloadGenerator(42).Generate();
        var second = new FuzzReloadGenerator(42).Generate();
        var different = new FuzzReloadGenerator(43).Generate();

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }
}
