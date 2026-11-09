namespace TinyCs.Tcs2c.Tests;

// 分岐で値を選ぶ式 (switch 式 / ?: / ?? / GetValueOrDefault(x)) の struct の値を
// copy 地点 (宣言の初期値 / 代入 / 引数 / 要素 / return) で受ける。Lua は arm が
// 格納先の table を返すので copy が要り、C は値渡しのまま同じ出力になる。
// 期待値は CoreCLR の実行結果
public partial class DifferentialTests
{
    [CFact]
    public void StructCopy_BranchValues()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct S { public int N; }
            public class H { public S F; public S G; public S? Opt; }
            public class P
            {
                static void Mut(S x) { x.N = 77; }
                static S Pick(H h, int k) { return k switch { 1 => h.F, _ => h.G }; }
                static S PickBody(H h, int k) => k switch { 1 => h.F, _ => h.G };
                public static void Main()
                {
                    var k = 1;
                    var c = true;
                    var h = new H();
                    var a = k switch { 1 => h.F, _ => h.G };
                    a.N = 9;
                    var b = (k switch { 1 => h.F, _ => h.G });
                    b.N = 9;
                    var t = c ? h.F : h.G;
                    t.N = 9;
                    h.Opt = new S { N = 3 };
                    var o = h.Opt ?? h.F;
                    o.N = 9;
                    var fb = new S { N = 4 };
                    var g = h.Opt.GetValueOrDefault(fb);
                    g.N = 9;
                    Console.WriteLine("opt:" + h.Opt.Value.N);
                    h.Opt = null;
                    var g2 = h.Opt.GetValueOrDefault(fb);
                    g2.N = 9;
                    Console.WriteLine("decl:" + h.F.N + ":" + (h.Opt.HasValue ? 1 : 0) + ":" + fb.N);
                    S x;
                    x = k switch { 1 => h.F, _ => h.G };
                    x.N = 9;
                    S y = new S();
                    y = k switch { 1 => h.F, _ => h.G };
                    y.N = 9;
                    Console.WriteLine("assign:" + h.F.N);
                    Mut(k switch { 1 => h.F, _ => h.G });
                    var l = new List<S>();
                    l.Add(k switch { 1 => h.F, _ => h.G });
                    var arr = new S[] { k switch { 1 => h.F, _ => h.G } };
                    h.F.N = 5;
                    Console.WriteLine("arg:" + l[0].N + ":" + arr[0].N + ":" + h.F.N);
                    h.F.N = 0;
                    var r = Pick(h, 1);
                    r.N = 9;
                    var r2 = PickBody(h, 1);
                    r2.N = 9;
                    Func<S> f = () => k switch { 1 => h.F, _ => h.G };
                    var r3 = f();
                    r3.N = 9;
                    Console.WriteLine("ret:" + h.F.N);
                    var n = k switch { 1 => c ? h.F : h.G, _ => h.G };
                    n.N = 9;
                    var m = c ? (k switch { 1 => h.F, _ => h.G }) : h.G;
                    m.N = 9;
                    var w = k switch { 1 => k switch { 1 => h.F, _ => h.G }, _ => h.G };
                    w.N = 9;
                    var fresh = k switch { 1 => new S { N = 1 }, _ => h.G };
                    S? sn = k switch { 1 => h.G, _ => null };
                    h.G.N = 4;
                    Console.WriteLine("nested:" + h.F.N + ":" + fresh.N + ":" + sn.Value.N);
                }
            }
            """, "P");
        Assert.Equal("opt:3\ndecl:0:0:4\nassign:0\narg:0:0:5\nret:0\nnested:0:1:0", output);
    }
}
