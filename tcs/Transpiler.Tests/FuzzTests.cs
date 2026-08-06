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

    // 文法枝が死んでいない (どの seed からも生成されない) ことの検出網
    [Fact]
    public void Generator_CoversExtendedGrammar()
    {
        var corpus = string.Join("\n", Enumerable.Range(0, 60)
            .Select(seed => new FuzzGenerator(seed).Generate()));

        Assert.Contains(".Substring(", corpus);
        Assert.Contains(".ToUpper()", corpus);
        Assert.Contains(".Contains(", corpus);
        Assert.Contains(".IndexOf(", corpus);
        Assert.Contains(".Length", corpus);
        Assert.Contains("$\"", corpus);
        Assert.Contains("static int F", corpus);
        Assert.Contains("static string F", corpus);
        Assert.Contains("static bool F", corpus);
        Assert.Contains("while (", corpus);
        Assert.Contains("switch", corpus);
        Assert.Contains("foreach (var ", corpus);
        Assert.Contains("break;", corpus);
        Assert.Contains("continue;", corpus);
        Assert.Contains("Dictionary<", corpus);
        Assert.Contains(".ContainsKey(", corpus);
        Assert.Contains(".TryGetValue(", corpus);
        Assert.Contains(".Sort()", corpus);
        Assert.Contains(".RemoveAt(", corpus);
    }

    // ユーザー定義オーバーロードはサブセット外 (TCS1001 MethodOverload —
    // Lua table の同名 key は last-write-wins) なので生成しないこと
    [Fact]
    public void Generator_NeverProducesOverloadedHelpers()
    {
        foreach (var seed in Enumerable.Range(0, 80))
        {
            var source = new FuzzGenerator(seed).Generate();
            var names = System.Text.RegularExpressions.Regex
                .Matches(source, @"static (?:int|bool|string) (F\d+)\(")
                .Select(match => match.Groups[1].Value).ToList();
            Assert.Equal(names.Distinct().Count(), names.Count);
        }
    }

    // 生成器の不変条件: 生成物はサブセット内 (診断が出たら生成器のバグ)。
    // 実行なしの transpile のみで検証する軽量ゲート
    [Fact]
    public void Generator_StaysInsideSubset()
    {
        foreach (var seed in Enumerable.Range(200, 30))
        {
            var source = new FuzzGenerator(seed).Generate();
            var result = Transpiler.TranspileWithDiagnostics([source],
                checkNaming: false);
            Assert.True(result.Errors.Count == 0,
                $"seed {seed} produced invalid C#:\n"
                + string.Join("\n", result.Errors) + "\n" + source);
            var violations = result.Warnings
                .Where(w => w.Contains("TCS100")).ToList();
            Assert.True(violations.Count == 0,
                $"seed {seed} produced diagnosed source:\n"
                + string.Join("\n", violations) + "\n" + source);
        }
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
        var helpers = generator.LastHelpers;

        var reduced = runner.Reduce(statements, InjectFault, helpers);

        Assert.True(reduced.Split('\n').Length <
            FuzzGenerator.Assemble(statements, helpers).Split('\n').Length,
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
            var reduced = runner.Reduce(generator.LastStatements,
                helpers: generator.LastHelpers);
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
