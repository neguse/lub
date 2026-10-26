namespace TinyCs.Tests;

using TinyCs.Tests.SpecConformance;

// hot reload fuzz。v1/v2 型定義ペアを生成し、同一 VM で
// v1 実行状態 → reload → 不変量検証 (retained 保持 / added=initializer /
// dropped=nil / identity / method swap / instance の OnReload は呼ばない) を行う。
// オラクルは differential でなく生成時に計算した期待値
public class FuzzReloadTests
{
    private static string Compose(FuzzReloadScenario scenario) =>
        TestHelper.ComposeReload(scenario.V1, scenario.StateLua, scenario.V2,
            scenario.AssertsLua);

    private static string ComposeRegistry(FuzzReloadScenario scenario) =>
        TestHelper.ComposeRegistryReload(scenario.V1, scenario.StateLua, scenario.V2,
            scenario.AssertsLua);

    // 検出網の自己検証用: reload を適用せず v1 のまま検証を走らせる
    private static string ComposeWithoutReload(FuzzReloadScenario scenario) =>
        $"{Transpiler.Transpile([scenario.V1], instanceRegistry: true)}\n{scenario.StateLua}\n" +
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

    // 同じ不変量を、session と registry (変わった module だけの batch と instance の移行) でも満たす
    [Fact]
    public void Scenarios_PassInvariants_ViaRegistry_ForFixedSeeds()
    {
        foreach (var seed in Enumerable.Range(0, 10))
        {
            var scenario = new FuzzReloadGenerator(seed).Generate();
            var result = TestHelper.RunLua(ComposeRegistry(scenario)).Trim();
            Assert.True(result == "ok",
                $"seed {seed}: {result}\n--- v1 ---\n{scenario.V1}\n" +
                $"--- v2 ---\n{scenario.V2}");
        }
    }

    // watch は変換と同じ compilation の形 (Shape) と出力の型の定義から chunk を組む。
    // 入力ごとに IL を作る PlanReload と、restart の判定と chunk が一致する
    [Fact]
    public void WatchPlan_MatchesPlanReload_ForFixedSeeds()
    {
        static TranspileResult Watch(string source) =>
            Transpiler.TranspileWithDiagnostics([source], instanceRegistry: true,
                cacheTypeLocals: false, reloadShape: true);
        foreach (var seed in Enumerable.Range(0, 10))
        {
            var scenario = new FuzzReloadGenerator(seed).Generate();
            var expected = HotReload.PlanReload([scenario.V1], [scenario.V2]);
            var v2 = Watch(scenario.V2);
            var actual = HotReload.Plan(
                HotReload.Analyze(Watch(scenario.V1).Shape!, v2.Shape!), v2.Definitions);
            Assert.Equal(expected.RestartReasons, actual.RestartReasons);
            Assert.True(expected.Chunk == actual.Chunk, $"seed {seed}: chunk differs");
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
                if (outcome == "ok")
                    outcome = "registry: " + TestHelper.RunLua(ComposeRegistry(scenario)).Trim();
                if (outcome == "registry: ok")
                    outcome = "ok";
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
