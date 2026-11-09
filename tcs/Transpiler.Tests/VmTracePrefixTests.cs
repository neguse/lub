namespace TinyCs.Tests;

// tcs vm --trace の対象のファイル (TcsVm.VmTrace.Matches)。prefix は path の segment 単位で比べ (ファイルそのものか、その directory)、
// '\' は '/' にし、先頭の `./` と prefix の末尾の `/` は外す (`./` はすべての入力に一致する)。
// VmTrace.Prefixes は process で 1 つなので、他のテストと並べて動かさない
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
