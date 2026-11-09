using TinyCs.Tests.SpecConformance;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// Transpiler.Tests の C4 fuzz の生成器 (FuzzGenerator) の program を、C (通常と GC stress) / VM (通常と GC stress) /
/// 実 .NET で走らせて stdout を比べる (AssertParity)。seed は TCS_FUZZ_SEED / TCS_FUZZ_COUNT で変えられる。
/// </summary>
public class FuzzDifferentialTests
{
    [CFact]
    public void GeneratedPrograms_AgreeAcrossBackends()
    {
        var baseSeed = int.TryParse(Environment.GetEnvironmentVariable("TCS_FUZZ_SEED"), out var s) ? s : 0;
        var count = int.TryParse(Environment.GetEnvironmentVariable("TCS_FUZZ_COUNT"), out var n) ? n : 12;
        var failures = new List<string>();
        for (var seed = baseSeed; seed < baseSeed + count; seed++)
        {
            var source = new FuzzGenerator(seed).Generate();
            try
            {
                Backends.AssertParity(source, "Program");
            }
            catch (Exception e)
            {
                failures.Add($"seed {seed}: {e.Message.Split('\n')[0]}");
            }
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
