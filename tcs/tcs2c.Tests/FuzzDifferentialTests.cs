using TinyCs.Tests.SpecConformance;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// Runs the programs of the Transpiler.Tests C4 fuzz generator (FuzzGenerator) on C (normal and GC stress) / VM (normal and GC stress) /
/// real .NET and compares stdout (AssertParity). The seed can be changed with TCS_FUZZ_SEED / TCS_FUZZ_COUNT.
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
