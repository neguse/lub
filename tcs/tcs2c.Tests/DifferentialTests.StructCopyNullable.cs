namespace TinyCs.Tcs2c.Tests;

// struct の T? (S?) の値を copy 地点 (宣言の初期値 / 代入 / 引数 / 要素 /
// field / return / ??= / 式位置の代入) で受ける。`c ? h.F : null` と `h?.F` の値は
// 格納先 h.F の値そのもので、Lua は値があれば中の struct を copy する。C は値渡しの
// まま同じ出力になる。期待値は CoreCLR の実行結果
public partial class DifferentialTests
{
    [CFact]
    public void StructCopy_NullableValues()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct S { public int N; }
            public class H { public S F; public S G; public S? Opt; public H Next; }
            public struct W { public S? O; public int K; }
            public class P
            {
                static S? Got;
                static S? Kept;
                static void Mut(S? x) { Got = x; }
                static S? Pick(H h, bool c) { return c ? h.F : null; }
                static S? PickBody(H h) => h?.F;
                static S? Prop { get; set; }
                static int V(S? x) => x.HasValue ? x.Value.N : -1;
                static S? Init = new H()?.F;
                public static void Main()
                {
                    var c = true;
                    var h = new H();
                    H nul = null;
                    S? a = c ? h.F : null;
                    var b = h?.F;
                    S? d = (c ? h.F : null);
                    S? e;
                    e = c ? h.F : null;
                    S? e2 = null;
                    e2 = h?.F;
                    S? none = null;
                    S? q = none ?? (c ? h.F : null);
                    S? q2 = null;
                    q2 ??= h?.F;
                    var nn = nul?.F;
                    S? sw = 1 switch { 1 => c ? h.F : null, _ => null };
                    Mut(c ? h.F : null);
                    var l = new List<S?>();
                    l.Add(h?.F);
                    var arr = new S?[] { c ? h.F : null };
                    var l2 = new List<S?> { h?.F };
                    var dict = new Dictionary<int, S?>();
                    dict[1] = h?.F;
                    dict.Add(2, c ? h.F : null);
                    var h2 = new H();
                    h2.Opt = h?.F;
                    var h3 = new H { Opt = c ? h.F : null };
                    Prop = h?.F;
                    var r = Pick(h, true);
                    var r2 = PickBody(h);
                    Func<S?> f = () => h?.F;
                    var r3 = f();
                    Func<H, S?> g = x => x?.F;
                    var r4 = g(h);
                    var w = (Kept = h?.F);
                    S? cast = (S?)(c ? h.F : null);
                    h.F.N = 9;
                    Console.WriteLine("decl:" + V(a) + ":" + V(b) + ":" + V(d) + ":" + V(nn) + ":" + V(sw));
                    Console.WriteLine("assign:" + V(e) + ":" + V(e2) + ":" + V(q) + ":" + V(q2) + ":" + V(Kept) + ":" + V(w) + ":" + V(cast));
                    Console.WriteLine("arg:" + V(Got) + ":" + V(l[0]) + ":" + V(arr[0]) + ":" + V(l2[0]) + ":" + V(dict[1]) + ":" + V(dict[2]));
                    Console.WriteLine("field:" + V(h2.Opt) + ":" + V(h3.Opt) + ":" + V(Prop));
                    Console.WriteLine("ret:" + V(r) + ":" + V(r2) + ":" + V(r3) + ":" + V(r4));
                    h.F.N = 0;
                    var chain = new H { Next = h };
                    var ch = chain.Next?.F;
                    S? fresh = c ? new S { N = 3 } : null;
                    S? copyOfNullable = fresh;
                    var ws = new W();
                    ws.O = c ? h.F : null;
                    var wa = new W[1];
                    wa[0].O = h?.F;
                    S? sw2 = 1 switch { 1 => h?.F, _ => null };
                    h.F.N = 5;
                    Console.WriteLine("slot:" + V(ws.O) + ":" + V(wa[0].O) + ":" + V(sw2) + ":" + V(Init));
                    Console.WriteLine("chain:" + V(ch) + ":" + V(fresh) + ":" + V(copyOfNullable) + ":" + h.F.N);
                }
            }
            """, "P");
        Assert.Equal("decl:0:0:0:-1:0\nassign:0:0:0:0:0:0:0\narg:0:0:0:0:0:0\n" +
            "field:0:0:0\nret:0:0:0:0\nslot:0:0:0:0\nchain:0:3:3:5", output);
    }
}
