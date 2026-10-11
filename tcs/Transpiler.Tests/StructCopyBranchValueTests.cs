namespace TinyCs.Tests;

// 分岐で値を選ぶ式 (switch 式 / ?: / ?? / GetValueOrDefault(x)) の値が struct の
// 格納先の値のとき、copy 地点 (il-spec §10: 宣言の初期値 / 代入 / 引数 / return /
// 要素) で受けた値を変えても格納先に残らない。期待値は CoreCLR の実行結果で、
// dotnet differential (TCS_DIFFERENTIAL=1) が突き合わせる
public class StructCopyBranchValueTests
{
    private const string S = """
        using System;
        using System.Collections.Generic;
        public struct S { public int N; }
        public class H { public S F; public S G; public S? Opt; }
        """;

    private static string Run(string source) =>
        TestHelper.TranspileAndRunWithRuntime(S + source, "T.Test()");

    [Fact]
    public void SwitchExpression_DeclarationAndAssignment_CopyArmValue()
    {
        Assert.Equal("0:0", Run("""
            public class T
            {
                public static string Test()
                {
                    var k = 1;
                    var h = new H();
                    var a = k switch { 1 => h.F, _ => h.G };
                    a.N = 9;
                    var b = (k switch { 1 => h.F, _ => h.G });
                    b.N = 9;
                    S x;
                    x = k switch { 1 => h.F, _ => h.G };
                    x.N = 9;
                    S? n = k switch { 1 => h.G, _ => null };
                    h.G.N = 9;
                    return h.F.N + ":" + n.Value.N;
                }
            }
            """));
    }

    [Fact]
    public void SwitchExpression_ArgumentAndElement_CopyArmValue()
    {
        Assert.Equal("0:0:0:5", Run("""
            public class T
            {
                static S Got;
                static void Mut(S x) { x.N = 77; Got = x; }
                public static string Test()
                {
                    var k = 1;
                    var h = new H();
                    Mut(k switch { 1 => h.F, _ => h.G });
                    var l = new List<S>();
                    l.Add(k switch { 1 => h.F, _ => h.G });
                    var arr = new S[] { k switch { 1 => h.F, _ => h.G } };
                    var before = h.F.N;
                    h.F.N = 5;
                    return before + ":" + l[0].N + ":" + arr[0].N + ":" + h.F.N;
                }
            }
            """));
    }

    [Fact]
    public void SwitchExpression_ReturnAndLambda_CopyArmValue()
    {
        Assert.Equal("0:0", Run("""
            public class T
            {
                static S Pick(H h, int k) { return k switch { 1 => h.F, _ => h.G }; }
                static S PickBody(H h, int k) => k switch { 1 => h.F, _ => h.G };
                public static string Test()
                {
                    var k = 1;
                    var h = new H();
                    var r = Pick(h, 1);
                    r.N = 9;
                    var r2 = PickBody(h, 1);
                    r2.N = 9;
                    Func<S> f = () => k switch { 1 => h.F, _ => h.G };
                    var r3 = f();
                    r3.N = 9;
                    Func<int, S> g = j => j switch { 1 => h.G, _ => h.F };
                    var l = new List<S> { g(1) };
                    h.G.N = 4;
                    return h.F.N + ":" + l[0].N;
                }
            }
            """));
    }

    // switch 式の arm に別の分岐式を入れ子にしても、格納先の値は copy。
    // 新しく作った値の arm (new / 入れ子の switch) はそのまま受け取れる
    [Fact]
    public void NestedBranches_CopyStoredArmValue()
    {
        Assert.Equal("0:0:1:2", Run("""
            public class T
            {
                public static string Test()
                {
                    var k = 1;
                    var c = true;
                    var h = new H();
                    var n = k switch { 1 => c ? h.F : h.G, _ => h.G };
                    n.N = 9;
                    var m = c ? (k switch { 1 => h.F, _ => h.G }) : h.G;
                    m.N = 9;
                    var w = k switch { 1 => k switch { 1 => h.F, _ => h.G }, _ => h.G };
                    w.N = 9;
                    var fresh = k switch { 1 => new S { N = 1 }, _ => h.G };
                    var made = k switch { 1 => k switch { _ => new S { N = 2 } }, _ => h.G };
                    var before = h.F.N;
                    h.F.N = 0;
                    return before + ":" + h.G.N + ":" + fresh.N + ":" + made.N;
                }
            }
            """));
    }

    [Fact]
    public void ConditionalAndCoalesce_CopyStoredValue()
    {
        Assert.Equal("0:3:4:3", Run("""
            public class T
            {
                public static string Test()
                {
                    var c = true;
                    var h = new H();
                    var t = c ? h.F : h.G;
                    t.N = 9;
                    h.Opt = new S { N = 3 };
                    var o = h.Opt ?? h.F;
                    o.N = 9;
                    var fb = new S { N = 4 };
                    var g = h.Opt.GetValueOrDefault(fb);
                    g.N = 9;
                    var opt = h.Opt.Value.N;
                    h.Opt = null;
                    var g2 = h.Opt.GetValueOrDefault(fb);
                    g2.N = 9;
                    h.Opt = new S { N = 3 };
                    var g3 = h.Opt.GetValueOrDefault();
                    g3.N = 9;
                    return h.F.N + ":" + opt + ":" + fb.N + ":" + h.Opt.Value.N;
                }
            }
            """));
    }
}
