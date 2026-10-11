namespace TinyCs.Tests;

// struct の T? (S?) の値 (`c ? h.F : null` / `h?.F`) は格納先 h.F の値そのもので、
// copy 地点 (il-spec §10: 宣言の初期値 / 代入 / 引数 / return / 要素 / field) で
// 値があれば中の struct を copy する。受けた後に h.F を変えても受けた値は変わらない。
// 期待値は CoreCLR の実行結果
public class StructCopyNullableValueTests
{
    private const string S = """
        using System;
        using System.Collections.Generic;
        public struct S { public int N; }
        public class H { public S F; public S? Opt; public H Next; }
        public static class V
        {
            public static int Of(S? x) => x.HasValue ? x.Value.N : -1;
        }
        """;

    private static string Run(string source) =>
        TestHelper.TranspileAndRunWithRuntime(S + source, "T.Test()");

    [Fact]
    public void DeclarationAndAssignment_CopyInnerValue()
    {
        Assert.Equal("0:0:0:-1:0:0:0:0", Run("""
            public class T
            {
                public static string Test()
                {
                    var c = true;
                    var h = new H();
                    H none = null;
                    S? a = c ? h.F : null;
                    var b = h?.F;
                    var n = none?.F;
                    S? e;
                    e = c ? h.F : null;
                    S? q = null;
                    q ??= h?.F;
                    S? empty = null;
                    S? o = empty ?? (c ? h.F : null);
                    S? x = null;
                    var w = (x = h?.F);
                    h.F.N = 9;
                    return V.Of(a) + ":" + V.Of(b) + ":" + V.Of(e) + ":" + V.Of(n) + ":"
                        + V.Of(q) + ":" + V.Of(o) + ":" + V.Of(x) + ":" + V.Of(w);
                }
            }
            """));
    }

    [Fact]
    public void ArgumentElementAndField_CopyInnerValue()
    {
        Assert.Equal("0:0:0:0:0:0", Run("""
            public class T
            {
                static S? Got;
                static void Keep(S? x) { Got = x; }
                public static string Test()
                {
                    var c = true;
                    var h = new H();
                    Keep(c ? h.F : null);
                    var l = new List<S?>();
                    l.Add(h?.F);
                    var arr = new S?[] { c ? h.F : null };
                    var d = new Dictionary<int, S?>();
                    d[1] = h?.F;
                    var h2 = new H();
                    h2.Opt = h?.F;
                    var h3 = new H { Opt = c ? h.F : null };
                    h.F.N = 9;
                    return V.Of(Got) + ":" + V.Of(l[0]) + ":" + V.Of(arr[0]) + ":"
                        + V.Of(d[1]) + ":" + V.Of(h2.Opt) + ":" + V.Of(h3.Opt);
                }
            }
            """));
    }

    [Fact]
    public void Return_CopyInnerValue()
    {
        Assert.Equal("0:0:0:0", Run("""
            public class T
            {
                static S? Pick(H h, bool c) { return c ? h.F : null; }
                static S? PickBody(H h) => h?.F;
                public static string Test()
                {
                    var h = new H();
                    var chain = new H { Next = h };
                    var r = Pick(h, true);
                    var r2 = PickBody(h);
                    Func<S?> f = () => chain.Next?.F;
                    var r3 = f();
                    var r4 = chain.Next?.F;
                    h.F.N = 9;
                    return V.Of(r) + ":" + V.Of(r2) + ":" + V.Of(r3) + ":" + V.Of(r4);
                }
            }
            """));
    }

    [Fact]
    public void StructSlotSwitchArmAndInitializer_CopyInnerValue()
    {
        Assert.Equal("0:0:0:0", Run("""
            public struct W { public S? O; }
            public class T
            {
                static H Shared = new H();
                static S? Init = Shared?.F;
                public static string Test()
                {
                    var c = true;
                    var h = Shared;
                    var w = new W();
                    w.O = c ? h.F : null;
                    var arr = new W[1];
                    arr[0].O = h?.F;
                    S? sw = 1 switch { 1 => h?.F, _ => null };
                    h.F.N = 9;
                    return V.Of(w.O) + ":" + V.Of(arr[0].O) + ":" + V.Of(sw) + ":" + V.Of(Init);
                }
            }
            """));
    }
}
