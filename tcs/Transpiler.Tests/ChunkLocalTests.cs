using System.Text;

namespace TinyCs.Tests;

// 型 table の chunk-local cache と Math 直接呼び出し (perf)。出力の形と、
// cache が global 公開・宣言順・200 local 上限・hot reload を壊さないこと
public class ChunkLocalTests
{
    [Fact]
    public void MathMembers_CallLuaBuiltinsDirectly_WithoutRuntime()
    {
        const string Source = """
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Min(3, 7) + ":" + Math.Max(3, 7) + ":" + Math.Abs(-42) + ":" +
                    (int)Math.Floor(2.7f) + ":" + (int)Math.Ceiling(2.1f) + ":" +
                    (int)Math.Sqrt(16.0f) + ":" + (int)Math.Round(2.5f) + ":" +
                    (int)Math.Round(3.5f);
            }
            """;
        var lua = Transpiler.Transpile(Source);
        Assert.Contains("math.sqrt(", lua);
        Assert.Contains("math.floor(", lua);
        Assert.DoesNotContain("Math.Sqrt(", lua);
        Assert.DoesNotContain("Math.Min(", lua);
        // Round は banker's rounding の wrapper が必要なので直接呼ばない
        Assert.Contains("Math.Round(", lua);

        // runtime (Math wrapper) を持たない chunk でも直接呼ぶ分は動く。
        // Round だけ runtime の wrapper を要するので別 run で確認する
        const string NoRound = """
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Min(3, 7) + ":" + Math.Max(3, 7) + ":" + Math.Abs(-42) + ":" +
                    (int)Math.Floor(2.7f) + ":" + (int)Math.Ceiling(2.1f) + ":" +
                    (int)Math.Sqrt(16.0f);
            }
            """;
        Assert.Equal("3:7:42:2:3:4", TestHelper.TranspileAndRun(NoRound, "T.Test()"));
        Assert.Equal("3:7:42:2:3:4:2:4",
            TestHelper.TranspileAndRunWithRuntime(Source, "T.Test()"));
    }

    [Fact]
    public void TypeTables_AreChunkLocalAndStillGlobal()
    {
        const string Source = """
            public class Counter
            {
                public static int Total = 0;
                public static void Add(int n) { Total += n; }
            }
            public struct V { public int X; }
            public enum Kind { A, B }
            public class T
            {
                public static int Test() { Counter.Add(5); return Counter.Total; }
            }
            """;
        var lua = Transpiler.Transpile(Source);
        Assert.Matches(@"(?m)^local Counter, V, Kind, T$", lua);
        Assert.Contains("Counter = {}; _ENV.Counter = Counter", lua);
        Assert.Equal("5", TestHelper.TranspileAndRun(Source, "T.Test()"));

        // host (別 chunk) は global 経由で同じ table を引ける
        var viaGlobal = TestHelper.RunLua(lua + "\nprint(_G.Counter.total, _G.T.test(), _G.Counter.total)");
        Assert.Equal("0\t5\t5", viaGlobal.Trim());
    }

    [Fact]
    public void TypeTables_ForwardReferenceAcrossDeclarationOrder()
    {
        const string Source = """
            public class A
            {
                public static int Test() { return B.Make().V + B.Count; }
            }
            public class B
            {
                public static int Count = 3;
                public int V = 4;
                public static B Make() { return new B(); }
            }
            """;
        var lua = Transpiler.Transpile(Source);
        // A の method は後方宣言の B を upvalue で引く (global 参照を残さない)
        Assert.Matches(@"(?m)^local A, B$", lua);
        Assert.Equal("7", TestHelper.TranspileAndRun(Source, "A.Test()"));
    }

    [Fact]
    public void TypeTables_BeyondBudget_FallBackToGlobals()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 150; i++)
            sb.Append($"public class C{i} {{ public static int V() {{ return {i}; }} }}\n");
        sb.Append("public class T { public static int Test() { return C0.V() + C149.V(); } }\n");
        var source = sb.ToString();
        var lua = Transpiler.Transpile(source);
        Assert.Contains("C0 = {}; _ENV.C0 = C0", lua);
        Assert.Contains("\nC149 = {}\n", lua);
        // 200 local 上限 (chunk 直下) に収まる
        Assert.Equal("149", TestHelper.TranspileAndRun(source, "T.Test()"));
    }

    [Fact]
    public void TypeTables_TopLevelLocalsReduceBudget()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < 90; i++)
            sb.Append($"var v{i} = {i};\n");
        sb.Append("System.Console.WriteLine(v89);\n");
        for (var i = 0; i < 150; i++)
            sb.Append($"public class C{i} {{ }}\n");
        // 実行すると chunk 直下の local は 35 (prelude) + 90 + cache 分
        var lua = Transpiler.Transpile(sb.ToString());
        Assert.Equal("89", TestHelper.RunLua(lua).Trim());
    }

    [Fact]
    public void TypeTables_NotCached_WhenDisabled()
    {
        var lua = Transpiler.Transpile(["public class T { }"], cacheTypeLocals: false);
        Assert.DoesNotContain("_ENV.", lua);
        Assert.DoesNotContain("tcs:type-locals", lua);
    }

    [Fact]
    public void Reload_V1CachedChunk_KeepsClassIdentityAndStatics()
    {
        const string V1 = """
            public class Config { public static int Speed = 3; }
            public class Player
            {
                public int Move() { return Config.Speed; }
            }
            """;
        const string V2 = """
            public class Config { public static int Speed = 3; }
            public class Player
            {
                public int Move() { return Config.Speed * 2; }
            }
            """;
        var script = $"{Transpiler.Transpile([V1])}\n" +
            "local p = Player.new()\nlocal cls, cfg = Player, Config\nConfig.speed = 10\n" +
            $"{HotReload.EmitReloadChunk([V1], [V2])}\n" +
            """
            assert(Player == cls and Config == cfg, "identity")
            assert(p:move() == 20, "v1 instance sees v2 body and live static")
            Config.speed = 4
            assert(p:move() == 8, "v2 body resolves the original Config")
            print("ok")
            """;
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }
}
