namespace TinyCs.Tests;

using TinyCs.Tests.SpecConformance;

public class FuzzTests
{
    [Fact]
    public void Generator_IsDeterministicPerSeed()
    {
        var first = new FuzzGenerator(42).Generate();
        var second = new FuzzGenerator(42).Generate();
        var different = new FuzzGenerator(43).Generate();

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    // 検出網の自己検証 (design doc §17 C4 gate): 生成 Lua へ故障を注入し、
    // differential が必ず検出することを確認する
    private static string InjectFault(string lua) => lua.Replace(
        "function Program.Main()",
        "function Program.Main()\n  print(\"FAULT\")",
        StringComparison.Ordinal);

    [Fact]
    public void Runner_DetectsInjectedLuaFault()
    {
        var runner = new FuzzRunner(SpecConformanceSweep.FindRepoRoot());
        var source = new FuzzGenerator(7).Generate();

        var healthy = runner.RunOne(source);
        Assert.True(healthy.Ok, healthy.Details);

        var faulty = runner.RunOne(source, InjectFault);
        Assert.False(faulty.Ok);
        Assert.Contains("FAULT", faulty.Details);
    }

    [Fact]
    public void Reducer_ShrinksFailingProgramToMinimum()
    {
        var runner = new FuzzRunner(SpecConformanceSweep.FindRepoRoot());
        var generator = new FuzzGenerator(7);
        generator.Generate();
        var statements = generator.LastStatements;

        var reduced = runner.Reduce(statements, InjectFault);

        Assert.True(reduced.Split('\n').Length <
            FuzzGenerator.Assemble(statements).Split('\n').Length,
            "reducer should shrink the program");
        Assert.False(runner.RunOne(reduced, InjectFault).Ok);
    }

    [FuzzFact]
    public void FuzzSweep_GeneratedProgramsMatchDotnet()
    {
        var repoRoot = SpecConformanceSweep.FindRepoRoot();
        var runner = new FuzzRunner(repoRoot);
        var baseSeed = int.TryParse(
            Environment.GetEnvironmentVariable("TCS_FUZZ_SEED"), out var seed)
            ? seed : 1000;
        var count = int.TryParse(
            Environment.GetEnvironmentVariable("TCS_FUZZ_COUNT"), out var n)
            ? n : 20;

        var failures = new List<string>();
        for (var offset = 0; offset < count; offset++)
        {
            var generator = new FuzzGenerator(baseSeed + offset);
            var source = generator.Generate();
            var outcome = runner.RunOne(source);
            if (outcome.Ok)
                continue;
            var reduced = runner.Reduce(generator.LastStatements);
            failures.Add($"seed {baseSeed + offset}: {outcome.Details}\n" +
                $"--- reduced repro ---\n{reduced}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{count} seeds failed\n"
            + string.Join("\n====\n", failures));
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class FuzzFactAttribute : FactAttribute
{
    public FuzzFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TCS_FUZZ") != "1")
            Skip = "Set TCS_FUZZ=1 to run the generative fuzz sweep.";
    }
}
