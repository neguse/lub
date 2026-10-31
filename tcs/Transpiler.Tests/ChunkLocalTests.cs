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
        Assert.DoesNotContain("Math.Sqrt(", lua);
        Assert.DoesNotContain("Math.Floor(", lua);
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
        var viaGlobal = TestHelper.RunLua(lua + "\nprint(_G.Counter.Total, _G.T.Test(), _G.Counter.Total)");
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

    // reload は v1 chunk と別 chunk で走る (TestHelper.ComposeReload)。v1 の
    // closure は chunk-local 経由で旧 table を引き続けるので、reload 後の
    // 型 table は class / struct / enum とも旧 identity でなければならない
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
        var script = TestHelper.ComposeReload(V1,
            "local p = Player.new()\nlocal cls, cfg = Player, Config\nConfig.Speed = 10",
            V2,
            """
            assert(Player == cls and Config == cfg, "identity")
            assert(p:Move() == 20, "v1 instance sees v2 body and live static")
            Config.Speed = 4
            assert(p:Move() == 8, "v2 body resolves the original Config")
            print("ok")
            """);
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    // enum member の追加は旧 table の中身を v2 で置き換える (値の変更は restart)
    [Fact]
    public void Reload_EnumMemberAdded_KeepsTableIdentity()
    {
        const string V1 = "public enum Kind { A, B }";
        const string V2 = "public enum Kind { A, B, C }";
        var script = TestHelper.ComposeReload(V1, "local kind = Kind", V2,
            """
            assert(Kind == kind, "enum table identity")
            assert(kind.C == 2, "v2 member through the v1 table")
            print("ok")
            """);
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void Reload_V1CachedChunk_UsesV2StructLayout()
    {
        const string V1 = """
            using System;
            public struct V { public int X; public V(int x) { X = x; } }
            public class Maker { public static Func<V> Make = () => new V(1); }
            """;
        const string V2 = """
            using System;
            public struct V
            {
                public int X; public int Y;
                public V(int x) { X = x; Y = 7; }
            }
            public class Maker
            {
                public static Func<V> Make = () => new V(1);
                public static int Get() => Make().X * 100 + Make().Y;
            }
            """;
        var script = TestHelper.ComposeReload(V1, "local v = V", V2,
            """
            assert(V == v, "struct table identity")
            assert(Maker.Get() == 107, "v1 closure builds the v2 layout")
            print("ok")
            """);
        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    // emit が素の global で呼ぶ Lua 標準 table と同名のローカルは写す
    [Fact]
    public void LocalsNamedLikeLuaStdGlobals_DoNotShadowEmittedCalls()
    {
        const string Source = """
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    int math = 3;
                    string @string = "ab";
                    var table = new List<int> { 1 };
                    table.Add(2);
                    Func<int, int> f = @string => (int)Math.Sqrt(16f) + @string;
                    return ((int)Math.Sqrt(16f) + math) + ":" + @string.ToUpper()
                        + ":" + table.Count + ":" + f(math);
                }
            }
            """;
        Assert.Equal("7:AB:2:7", TestHelper.TranspileAndRunWithRuntime(Source, "T.Test()"));
    }

    [Fact]
    public void LocalsNamedTcsMath_DoNotShadowMathModule()
    {
        // 超越関数の呼び出し先 (host が登録する global の tcs_math) と同名の
        // 引数・ローカル (#214)
        const string Source = """
            public class T
            {
                public static float F(float tcs_math) => (float)System.Math.Cos(tcs_math) + tcs_math;
                public static string Test()
                {
                    float tcs_math = 0f;
                    return F(tcs_math) + ":" + (float)System.Math.Exp(tcs_math);
                }
            }
            """;
        Assert.Equal("1:1", TestHelper.TranspileAndRunWithRuntime(Source, "T.Test()"));
    }

    [Fact]
    public void LocalsNamedTcsUtf8_DoNotShadowUtf8Module()
    {
        const string Source = """
            public class T
            {
                public static string Test()
                {
                    int tcs_utf8 = 1;
                    foreach (var r in "a".EnumerateRunes()) tcs_utf8 += r.Value;
                    return tcs_utf8.ToString();
                }
            }
            """;
        Assert.Equal("98", TestHelper.TranspileAndRunWithRuntime(Source, "T.Test()"));
    }

    [Fact]
    public void TypeLocalsPlaceholder_InUserStringLiteral_IsPreserved()
    {
        const string Source = """
            public class T
            {
                public static string Test() => "-- tcs:type-locals";
            }
            """;
        Assert.Equal("-- tcs:type-locals", TestHelper.TranspileAndRun(Source, "T.Test()"));
    }

    [Fact]
    public void NameofRenamedLocal_ReturnsSourceName()
    {
        const string Source = """
            public class T
            {
                public static string Test(int math, int end) =>
                    nameof(math) + ":" + nameof(end) + ":" + (math + end);
            }
            """;
        Assert.Equal("math:end:3", TestHelper.TranspileAndRun(Source, "T.Test(1, 2)"));
    }
}
