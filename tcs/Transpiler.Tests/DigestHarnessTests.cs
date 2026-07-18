using System.Globalization;

namespace TinyCs.Tests;

// T215: digest harness — spike (../luo/spike/CONTRACT.md) と同一仕様の
// kernel を TinyC# で記述し、lua32 (f32) 実行結果の FNV-1a digest を固定する。
// 2 backend (Lua / C) 一致検証の tcs 側正本。期待値は初回実測で記録し、
// 変化 = 数値意味論の退行として検出する。spike C 変種との突き合わせは
// luo 側 run.sh の digest と比較する (CONTRACT の LCG / 演算列が同一)。
public class DigestHarnessTests
{
    private static string RunKernel(string fileName, string entryClass)
    {
        var path = TestHelper.FindProjectFile(
            $"Transpiler.Tests/DigestKernels/{fileName}");
        var lua = Transpiler.Transpile([File.ReadAllText(path)]);
        return TestHelper.RunLua($"{lua}\n{entryClass}.Main()",
            TimeSpan.FromMinutes(2));
    }

    private static string Fnv1a32(string stdout)
    {
        uint h = 2166136261;
        foreach (var line in stdout.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var value = float.Parse(line, CultureInfo.InvariantCulture);
            var bits = BitConverter.SingleToUInt32Bits(value);
            for (var i = 0; i < 4; i++)
            {
                h = (h ^ ((bits >> (8 * i)) & 0xFF)) * 16777619;
            }
        }
        return h.ToString("x8", CultureInfo.InvariantCulture);
    }

    [Fact]
    public void SpriteUpdate_DigestIsStable()
    {
        var digest = Fnv1a32(RunKernel("sprite_update.cs", "SpriteUpdate"));
        Assert.Equal("e8814b32", digest);
    }

    [Fact]
    public void SpawnChurn_DigestIsStable()
    {
        var digest = Fnv1a32(RunKernel("spawn_churn.cs", "SpawnChurn"));
        Assert.Equal("9274159d", digest);
    }

    [Fact]
    public void Particles_DigestIsStable()
    {
        var digest = Fnv1a32(RunKernel("particles.cs", "Particles"));
        Assert.Equal("8bf97e09", digest);
    }

    // T219: struct 版 particles は SoA 版と同一演算列 → 同一 digest
    [Fact]
    public void ParticlesStruct_MatchesSoaDigest()
    {
        var digest = Fnv1a32(RunKernel("particles_struct.cs", "ParticlesStruct"));
        Assert.Equal("8bf97e09", digest);
    }
}
