namespace TinyCs.Tcs2c.Tests;

// List<T>.FindIndex / RemoveAll (BCL の List の member) と TinySystem.Linq の MinBy / MaxBy。
// RemoveAll は各要素に述語を 1 回ずつ先頭から呼び、残す要素の順を保つ。MinBy / MaxBy は
// key の等しい要素で先に出た方、float の NaN の key は最小、string の key は ordinal で null が最小。
// 期待値は同じ program を CoreCLR で実行した stdout (空の MinBy は "before" の後に停止する)
public partial class DifferentialTests
{
    [CFact]
    public void FindIndexRemoveAll_MinByMaxBy_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public readonly record struct V(int A, float B, string N);
            public static class P
            {
                public static void Main()
                {
                    var list = new List<int> { 4, 7, 2, 7, 9 };
                    Console.WriteLine(list.FindIndex(x => x == 7) + " " + list.FindIndex(x => x > 100));
                    var seen = "";
                    var removed = list.RemoveAll(x => { seen += x + ","; return x == 7; });
                    var rest = "";
                    foreach (var x in list) rest += x + ",";
                    Console.WriteLine(removed + " " + seen + " " + rest);
                    Console.WriteLine(list.RemoveAll(x => false) + " " + new List<int>().FindIndex(x => true));
                    float zero = 0f;
                    var vs = new List<V> { new V(2, 1.5f, "b"), new V(1, zero / zero, "a"), new V(1, 0.5f, "c"), new V(3, 2.5f, "a") };
                    Console.WriteLine(vs.MinBy(v => v.A).N + vs.MaxBy(v => v.A).N + vs.MinBy(v => v.B).N
                        + vs.MaxBy(v => v.B).N + vs.MinBy(v => v.N).A + vs.MaxBy(v => v.N).N);
                    var keys = "";
                    vs.MaxBy(v => { keys += v.A; return v.A; });
                    Console.WriteLine(keys);
                }
            }
            """, "P");
        Assert.Equal("""
            1 -1
            2 4,7,2,7,9, 4,2,9,
            0 -1
            aaaa1c
            2113
            """, output);
    }

    [CTheory]
    [InlineData("new List<int>().MinBy(x => x)")]
    [InlineData("new List<string>().MaxBy(s => s.Length).Length")]
    public void MinByMaxBy_OnEmpty_FaultsAfterPrecedingOutput(string call)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine({{call}});
                }
            }
            """, "P"));
    }
}
