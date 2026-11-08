namespace TinyCs.Tests;

using TinyCs.Tests.SpecConformance;

// hot reload fuzz の生成器。scenario は tcs2c.Tests の VmReloadTests が tcs VM の reload に当てて不変量を確かめる
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
