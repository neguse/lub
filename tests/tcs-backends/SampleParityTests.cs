namespace TinyCs.Tcs2c.Tests;

// Runs samples/*.cs on C and the VM and requires stdout to match the expected strings (same as tests/tcs/SampleE2ETests).
// The entry is given by a wrapper class
public class SampleParityTests
{
    private static string Entry(string expr) =>
        "public static class TcsEntry { public static void Main() { " +
        $"System.Console.WriteLine({expr}); }} }}";

    [CTheory]
    [InlineData("tests/tcs/samples/hello.cs",
        "Hello.Greet(\"TinyC#\") + \",\" + Hello.Add(2, 3).ToString()", "Hello, TinyC#!,5")]
    [InlineData("tests/tcs/samples/game.cs", "Battle.Run()", "Dragon: HP=145 [attacking] | alive=2")]
    [InlineData("tests/tcs/samples/inventory.cs", "Game.Test()", "Items=4 Total=330 Best=Sword Shield=1")]
    [InlineData("tests/tcs/samples/entity.cs", "EntitySample.Run()", "Slime:enemy@6,2 HP=13")]
    [InlineData("tests/tcs/samples/statemachine.cs", "StateMachineSample.Run()", "open,open,locked,closed")]
    [InlineData("tests/tcs/samples/collision.cs", "CollisionSample.Run()", "hit,miss,hit")]
    public void Sample_MatchesExpected(string sample, string expr, string expected)
    {
        var output = Backends.AssertParity(
            [Backends.Sample(sample), Entry(expr)], "TcsEntry");
        Assert.Equal(expected, output);
    }

    [CFact]
    public void DigestKernels_MatchFixedDigests()
    {
        // The same fixed digests as verify-digests.sh and tests/tcs-vm/run-tests.sh. Here the C-side values (including GC) are checked.
        // particles_struct has the same sequence of operations as particles, so the digest is the same
        var expected = new Dictionary<string, (string Entry, string Digest)>
        {
            ["sprite_update"] = ("SpriteUpdate", "e8814b32"),
            ["spawn_churn"] = ("SpawnChurn", "9274159d"),
            ["particles"] = ("Particles", "8bf97e09"),
            ["particles_struct"] = ("ParticlesStruct", "8bf97e09"),
        };
        foreach (var (kernel, (entry, digest)) in expected)
        {
            var source = Backends.Sample($"tests/tcs/DigestKernels/{kernel}.cs");
            var c = Backends.EmitC([source], entry, digestF32: true);
            Assert.Equal(digest, Backends.CompileAndRunC(c));
        }
    }
}
