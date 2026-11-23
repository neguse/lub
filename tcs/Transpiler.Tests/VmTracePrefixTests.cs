namespace TinyCs.Tests;

// Files targeted by tcs vm --trace (TcsVm.VmTrace.Matches). Prefixes are compared by path segment (the file itself or its directory),
// '\' becomes '/', and the leading `./` and the trailing `/` of the prefix are removed (`./` matches every input).
// VmTrace.Prefixes is one per process, so these tests do not run in parallel with other tests
[Collection(VmTraceCollection.Name)]
public class VmTracePrefixTests
{
    private static string[] Traced(string prefix, params string[] paths)
    {
        var saved = TcsVm.VmTrace.Prefixes;
        try
        {
            TcsVm.VmTrace.Prefixes = [prefix];
            return [.. paths.Where(TcsVm.VmTrace.Matches)];
        }
        finally
        {
            TcsVm.VmTrace.Prefixes = saved;
        }
    }

    [Theory]
    [InlineData("samples/game", "samples/game/Game.cs")]
    [InlineData("samples/game/", "samples/game/Game.cs")]
    [InlineData("./samples/game/", "samples/game/Game.cs")]
    [InlineData("samples/game/Game.cs", "samples/game/Game.cs")]
    [InlineData("samples/game/Game", "")]
    [InlineData("samples/gameover", "samples/gameover/Over.cs")]
    [InlineData("./", "samples/game/Game.cs,samples/gameover/Over.cs")]
    public void PrefixMatchesWholePathSegments(string prefix, string expected)
    {
        Assert.Equal(expected, string.Join(",", Traced(prefix, "samples/game/Game.cs", "samples/gameover/Over.cs")));
    }

    [Fact]
    public void PrefixIsMatchedAgainstNormalizedPath()
    {
        Assert.Equal([@"samples\game\Game.cs"], Traced("samples/game/", @"samples\game\Game.cs", @"samples\other\Other.cs"));
        Assert.Equal(["./samples/game/Game.cs"], Traced(@"samples\game", "./samples/game/Game.cs"));
    }

    [Fact]
    public void NoPrefix_TracesNothing()
    {
        var saved = TcsVm.VmTrace.Prefixes;
        try
        {
            TcsVm.VmTrace.Prefixes = [];
            Assert.False(TcsVm.VmTrace.Matches("samples/game/Game.cs"));
        }
        finally
        {
            TcsVm.VmTrace.Prefixes = saved;
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class VmTraceCollection
{
    public const string Name = "VmTrace";
}
