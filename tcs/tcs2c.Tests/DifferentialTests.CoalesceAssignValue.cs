namespace TinyCs.Tcs2c.Tests;

// 式位置の `a ??= b` の値は代入後の a で、a が `T?` なら型は `T` (C# の ??=)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void CoalesceAssignment_ValueIsUnderlyingType()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct S { public int N; }
            public struct Slot { public int? Opt; }
            public class X
            {
                public int? Opt;
                public Slot Data;
                public S? SOpt;
                public int? Auto { get; set; }
                int? backing;
                public int? Prop { get { return backing; } set { backing = value; } }
            }
            public class P
            {
                static X Shared = new X();
                static int? Static;
                static X Get() { return Shared; }
                static int Twice(int v) { return v * 2; }
                static void Mut(S v) { v.N = 77; }
                public static void Main()
                {
                    var x = new X();
                    int r = (x.Opt ??= 7);
                    int r2 = (x.Opt ??= 9);
                    int? loc = null;
                    int r3 = (loc ??= 3);
                    int r4 = (x.Prop ??= 4);
                    int r5 = (x.Auto ??= 5);
                    int r6 = (x.Data.Opt ??= 6);
                    int r7 = (Get().Opt ??= 8);
                    int r8 = (Static ??= 10);
                    Console.WriteLine(r + ":" + r2 + ":" + r3 + ":" + r4 + ":" + r5 + ":" + r6 + ":" + r7 + ":" + r8);
                    Console.WriteLine(Twice(x.Opt ??= 1) + ":" + (loc ??= 1) + ":" + x.Opt.Value + ":" + Static.Value);
                    var src = new S { N = 5 };
                    S s = (x.SOpt ??= src);
                    S? o = null;
                    Mut(o ??= src);
                    Console.WriteLine(s.N + ":" + x.SOpt.Value.N + ":" + o.Value.N + ":" + src.N);
                    string? name = null;
                    string n = (name ??= "a");
                    Console.WriteLine(n + name);
                }
            }
            """, "P");
        Assert.Equal("7:7:3:4:5:6:8:10\n14:3:7:10\n5:5:5:5\naa", output);
    }
}
