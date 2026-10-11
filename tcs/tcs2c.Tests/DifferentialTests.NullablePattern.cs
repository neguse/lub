namespace TinyCs.Tcs2c.Tests;

// struct 型の pattern (型 / 宣言 / 型を書いた property pattern) を struct と
// `T?` (Nullable の struct) の入力に当てるとき。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void TypedPropertyPattern_NullableInput()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct Pt { public int X; public int Y; }
            public class Holder { public Pt? At; }
            public class P
            {
                static int calls;
                static Pt? Get(Pt? v) { calls++; return v; }
                static string Pick(Pt? p) => p switch { Pt { X: 2 } => "two", Pt { Y: 5 } => "y5", null => "null", _ => "other" };
                public static void Main()
                {
                    Pt? p = new Pt { X = 2 };
                    Pt? q = null;
                    Pt? r = new Pt { X = 3, Y = 5 };
                    Console.WriteLine((p is Pt { X: 2 } ? 1 : 0) + "," + (q is Pt { X: 2 } ? 1 : 0) + "," + (r is Pt { X: 2 } ? 1 : 0));
                    Console.WriteLine((p is Pt { } ? 1 : 0) + "," + (q is Pt { } ? 1 : 0));
                    Console.WriteLine((p is not Pt { X: 2 } ? 1 : 0) + "," + (q is not Pt { X: 2 } ? 1 : 0));
                    Console.WriteLine((r is Pt { X: 3, Y: 5 } ? 1 : 0) + "," + (r is Pt { X: 3, Y: 4 } ? 1 : 0));
                    Console.WriteLine(Pick(p) + "," + Pick(q) + "," + Pick(r) + "," + Pick(new Pt()));
                    var h = new Holder { At = new Pt { X = 2 } };
                    var e = new Holder();
                    Console.WriteLine((h is { At: Pt { X: 2 } } ? 1 : 0) + "," + (e is { At: Pt { X: 2 } } ? 1 : 0));
                    Console.WriteLine((Get(r) is Pt { X: 3, Y: 5 } ? 1 : 0) + ":" + calls);
                }
            }
            """, "P");
        Assert.Equal("1,0,0\n1,0\n0,1\n1,0\ntwo,null,y5,other\n1,0\n1:1", output);
    }

    [CFact]
    public void StructTypePattern_BindsCopyOfValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct Pt { public int X; }
            public readonly record struct Rp(int a);
            public class P
            {
                static int calls;
                static Pt? Get(Pt? v) { calls++; return v; }
                static int Sw(Pt? p) => p switch { Pt v when v.X > 5 => 100 + v.X, Pt v => v.X, _ => -1 };
                static string Stmt(Pt? p)
                {
                    switch (p)
                    {
                        case Pt v when v.X == 0: return "zero";
                        case Pt: return "pt";
                        default: return "none";
                    }
                }
                public static void Main()
                {
                    Pt? p = new Pt { X = 2 };
                    Pt? q = null;
                    Console.WriteLine((p is Pt ? 1 : 0) + "," + (q is Pt ? 1 : 0) + "," + (q is not Pt ? 1 : 0));
                    if (p is Pt v) { v.X = 7; Console.WriteLine(v.X + "," + p.Value.X); }
                    if (q is Pt u) Console.WriteLine("bad " + u.X);
                    Console.WriteLine(Sw(p) + "," + Sw(new Pt { X = 9 }) + "," + Sw(null));
                    Console.WriteLine(Stmt(new Pt()) + "," + Stmt(p) + "," + Stmt(null));
                    Pt w = new Pt { X = 4 };
                    if (w is Pt c) { c.X = 9; Console.WriteLine(w.X + "," + c.X); }
                    Console.WriteLine((w is Pt { X: 4 } ? 1 : 0) + "," + (w is Pt ? 1 : 0));
                    Rp? r = new Rp(3);
                    if (r is Rp rv) Console.WriteLine(rv.a);
                    Console.WriteLine((Get(p) is Pt g ? g.X : -1) + ":" + calls);
                }
            }
            """, "P");
        Assert.Equal("1,0,1\n7,2\n2,109,-1\nzero,pt,none\n4,9\n1,1\n3\n2:1", output);
    }

    // and の右辺は左辺が絞った struct の値を照合する。同名の designation を持つ
    // arm は arm ごとに copy を束縛する (前の arm の when での変異を見ない)
    [CFact]
    public void StructPattern_AndNarrowingAndPerArmCopy()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct Pt { public int X; }
            public class Holder { public Pt? At; }
            public class P
            {
                static int Arms(Pt? p) => p switch
                {
                    Pt v when (v.X = 100) < 0 => -1,
                    Pt v => v.X,
                    _ => -2,
                };
                static int Cases(Pt? p)
                {
                    switch (p)
                    {
                        case Pt v when (v.X = 100) < 0: return -1;
                        case Pt v: return v.X;
                        default: return -2;
                    }
                }
                static int Plain(Pt p) => p switch { Pt v when (v.X = 50) < 0 => -1, Pt v => v.X };
                public static void Main()
                {
                    Pt? p = new Pt { X = 2 };
                    Pt? q = null;
                    Console.WriteLine((p is Pt and { X: 2 } ? 1 : 0) + "," + (q is Pt and { X: 2 } ? 1 : 0) + "," + (p is Pt and not { X: 3 } ? 1 : 0) + "," + (p is Pt and { X: 3 } ? 1 : 0));
                    var h = new Holder { At = new Pt { X = 4 } };
                    var e = new Holder();
                    Console.WriteLine((h is { At: Pt { X: 4 } and { X: > 3 } } ? 1 : 0) + "," + (e is { At: Pt { X: 4 } and { X: > 3 } } ? 1 : 0));
                    Console.WriteLine((p is { } and { X: 2 } ? 1 : 0) + "," + (p is not null and { X: 2 } ? 1 : 0));
                    Console.WriteLine(Arms(new Pt { X = 1 }) + "," + Arms(null) + "," + Cases(new Pt { X = 1 }) + "," + Cases(null) + "," + Plain(new Pt { X = 7 }));
                }
            }
            """, "P");
        Assert.Equal("1,0,1,0\n1,0\n1,1\n1,-2,1,-2,7", output);
    }
}
