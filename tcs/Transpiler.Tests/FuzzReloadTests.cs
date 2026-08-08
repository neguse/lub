namespace TinyCs.Tests;

using TinyCs.Tests.SpecConformance;

// T235: hot reload fuzz。v1/v2 型定義ペアを生成し、同一 VM で
// v1 実行状態 → reload → 不変量検証 (retained 保持 / added=initializer /
// dropped=nil / identity / method swap / OnReload 1 回) を行う。
// オラクルは differential でなく生成時に計算した期待値
public class FuzzReloadTests
{
    private static string Compose(FuzzReloadScenario scenario) =>
        $"{Transpiler.Transpile([scenario.V1])}\n{scenario.StateLua}\n" +
        $"{HotReload.EmitReloadChunk([scenario.V1], [scenario.V2])}\n" +
        scenario.AssertsLua;

    // 検出網の自己検証用: reload を適用せず v1 のまま検証を走らせる
    private static string ComposeWithoutReload(FuzzReloadScenario scenario) =>
        $"{Transpiler.Transpile([scenario.V1])}\n{scenario.StateLua}\n" +
        scenario.AssertsLua;

    [Fact]
    public void Generator_IsDeterministicPerSeed()
    {
        var first = new FuzzReloadGenerator(42).Generate();
        var second = new FuzzReloadGenerator(42).Generate();
        var different = new FuzzReloadGenerator(43).Generate();

        Assert.Equal(first, second);
        Assert.NotEqual(first, different);
    }

    // 常設ゲート: 固定 seed のシナリオが不変量を満たす
    [Fact]
    public void Scenarios_PassInvariants_ForFixedSeeds()
    {
        foreach (var seed in Enumerable.Range(0, 10))
        {
            var scenario = new FuzzReloadGenerator(seed).Generate();
            var result = TestHelper.RunLua(Compose(scenario)).Trim();
            Assert.True(result == "ok",
                $"seed {seed}: {result}\n--- v1 ---\n{scenario.V1}\n" +
                $"--- v2 ---\n{scenario.V2}");
        }
    }

    // 生成器は毎シナリオ「method body 変更 + added field」を必ず含むので、
    // migration が走らなければ不変量が破れて検出できる
    [Fact]
    public void InvariantNet_DetectsMissingMigration()
    {
        foreach (var seed in Enumerable.Range(0, 5))
        {
            var scenario = new FuzzReloadGenerator(seed).Generate();
            Assert.ThrowsAny<Exception>(() =>
                TestHelper.RunLua(ComposeWithoutReload(scenario)));
        }
    }

    [FuzzFact]
    public void FuzzReloadSweep_ScenariosSatisfyInvariants()
    {
        var baseSeed = int.TryParse(
            Environment.GetEnvironmentVariable("TCS_FUZZ_SEED"), out var seed)
            ? seed : 1000;
        var count = int.TryParse(
            Environment.GetEnvironmentVariable("TCS_FUZZ_COUNT"), out var n)
            ? n : 20;

        var failures = new List<string>();
        for (var offset = 0; offset < count; offset++)
        {
            var scenario = new FuzzReloadGenerator(baseSeed + offset).Generate();
            string outcome;
            try
            {
                outcome = TestHelper.RunLua(Compose(scenario)).Trim();
            }
            catch (Exception e)
            {
                outcome = e.Message;
            }
            if (outcome == "ok")
                continue;
            failures.Add($"seed {baseSeed + offset}: {outcome}\n" +
                $"--- v1 ---\n{scenario.V1}\n--- v2 ---\n{scenario.V2}\n" +
                $"--- state ---\n{scenario.StateLua}\n" +
                $"--- asserts ---\n{scenario.AssertsLua}");
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count}/{count} seeds failed\n"
            + string.Join("\n====\n", failures));
    }
}
