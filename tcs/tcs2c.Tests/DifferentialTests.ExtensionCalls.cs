namespace TinyCs.Tcs2c.Tests;

// 拡張メソッドの呼び方 (拡張の形 `x.M()`、static の形 `Ext.M(x)`、`?.` 経由)
// はどれも同じ静的呼び出しになる。期待値は同じ program を CoreCLR で実行した
// stdout
public partial class DifferentialTests
{
    [CFact]
    public void Extensions_StaticFormAndConditionalAccess()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public struct V { public int X; }
            public static class Ext
            {
                public static int Twice(this int x) { return x * 2; }
                public static int Len(this string s, int extra = 1) { return s.Length + extra; }
                public static int Bump(this V v) { v.X++; return v.X; }
            }
            public class H { public string S = "abc"; public string N = null; public V? MaybeV = new V { X = 4 }; }
            public static class P
            {
                public static void Main()
                {
                    int sq = 5;
                    Console.WriteLine(Ext.Twice(sq) + ":" + sq.Twice() + ":" + Ext.Len("ab") + ":" + Ext.Len("ab", 5));
                    var v = new V { X = 1 };
                    Console.WriteLine(Ext.Bump(v) + ":" + v.X + ":" + v.Bump() + ":" + v.X);
                    var h = new H();
                    Console.WriteLine((h.S?.Len() ?? -1) + ":" + (h.N?.Len() ?? -1) + ":" + (h.S?.Len(3) ?? -1));
                    Console.WriteLine(h.MaybeV?.Bump() ?? -1);
                    var l = new List<int> { 1, 2, 3 };
                    Console.WriteLine(Enumerable.Count(l, x => true) + ":" + Enumerable.Sum(l) + ":" + Enumerable.Count(l, x => x > 1));
                }
            }
            """, "P");
        Assert.Equal("""
            10:10:3:7
            2:1:2:1
            4:-1:6
            5
            3:6:2
            """.ReplaceLineEndings("\n"), output);
    }
}
