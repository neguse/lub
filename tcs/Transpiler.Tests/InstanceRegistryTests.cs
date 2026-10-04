namespace TinyCs.Tests;

// hot reload 用 weak instance registry (`__tcs_instances`) は opt-in。
// 既定の出力は registry を持たず、構築時の登録コストを払わない
[Collection(ConsoleCollection.Name)]
public class InstanceRegistryTests
{
    private const string Source = """
        public class Player
        {
            public int Hp = 10;
        }
        public class Hero : Player
        {
            public int Mana = 5;
        }
        public record Pt(int X, int Y);
        """;

    [Fact]
    public void Default_OmitsInstanceRegistry()
    {
        var lua = Transpiler.Transpile([Source]);
        var script = $$"""
            {{lua}}
            local p = Player.new()
            local h = Hero.new()
            local q = Pt.new(1, 2)
            print(__tcs_instances == nil and h.hp == 10 and h.mana == 5
              and q.x == 1)
            """;
        Assert.Equal("true", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void InstanceRegistry_RegistersMostDerivedClass()
    {
        var lua = Transpiler.Transpile([Source], instanceRegistry: true);
        var script = $$"""
            {{lua}}
            local p = Player.new()
            local h = Hero.new()
            local q = Pt.new(1, 2)
            print(__tcs_instances[p] == Player and __tcs_instances[h] == Hero
              and __tcs_instances[q] == Pt)
            """;
        Assert.Equal("true", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void Cli_InstanceRegistryOption_EmitsRegistry()
    {
        var temp = Directory.CreateTempSubdirectory("tcs-registry-");
        try
        {
            var inputPath = Path.Combine(temp.FullName, "app.cs");
            File.WriteAllText(inputPath, Source);
            var outputPath = Path.Combine(temp.FullName, "app.lua");

            var exitCode = Program.Main(
                [inputPath, "-o", outputPath, "--no-runtime", "--instance-registry"]);

            Assert.Equal(0, exitCode);
            var lua = File.ReadAllText(outputPath);
            var script = $"{lua}\nlocal h = Hero.new()\nprint(__tcs_instances[h] == Hero)";
            Assert.Equal("true", TestHelper.RunLua(script).Trim());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }
}
