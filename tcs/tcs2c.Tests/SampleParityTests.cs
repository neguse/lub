namespace TinyCs.Tcs2c.Tests;

// samples/*.cs を 2 backend で実行し stdout 一致を要求する (Lua 側の期待は
// Transpiler.Tests/SampleE2ETests と同じ)。entry は wrapper class で与える
public class SampleParityTests
{
    private static string Entry(string expr) =>
        "public static class TcsEntry { public static void Main() { " +
        $"System.Console.WriteLine({expr}); }} }}";

    [CTheory]
    [InlineData("samples/hello.cs",
        "Hello.Greet(\"TinyC#\") + \",\" + Hello.Add(2, 3).ToString()", "Hello, TinyC#!,5")]
    [InlineData("samples/game.cs", "Battle.Run()", "Dragon: HP=145 [attacking] | alive=2")]
    [InlineData("samples/inventory.cs", "Game.Test()", "Items=4 Total=330 Best=Sword Shield=1")]
    [InlineData("samples/entity.cs", "EntitySample.Run()", "Slime:enemy@6,2 HP=13")]
    [InlineData("samples/statemachine.cs", "StateMachineSample.Run()", "open,open,locked,closed")]
    [InlineData("samples/collision.cs", "CollisionSample.Run()", "hit,miss,hit")]
    public void Sample_MatchesLuaAndExpected(string sample, string expr, string expected)
    {
        var output = Backends.AssertParity(
            [Backends.Sample(sample), Entry(expr)], "TcsEntry");
        Assert.Equal(expected, output);
    }

    [CFact]
    public void DigestKernels_StillMatchLuaDigests()
    {
        // verify-digests.sh と同じ 3 kernel。Lua 側 digest は run-tests の
        // 既存ゲートが守るので、ここでは C 側 (GC 込み) の値を固定する
        var expected = new Dictionary<string, (string Entry, string Digest)>
        {
            ["sprite_update"] = ("SpriteUpdate", "e8814b32"),
            ["spawn_churn"] = ("SpawnChurn", "9274159d"),
            ["particles"] = ("Particles", "8bf97e09"),
        };
        foreach (var (kernel, (entry, digest)) in expected)
        {
            var source = Backends.Sample($"Transpiler.Tests/DigestKernels/{kernel}.cs");
            var c = Backends.EmitC([source], entry, digestF32: true);
            Assert.Equal(digest, Backends.CompileAndRunC(c));
        }
    }
}
