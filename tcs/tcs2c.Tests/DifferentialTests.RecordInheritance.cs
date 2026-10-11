namespace TinyCs.Tcs2c.Tests;

// record class の == のうち、Nullable の struct field と基底 record の
// field (C# の合成 Equals は基底の Equals を呼ぶ)、派生 record の構築
// (基底の primary ctor 引数、基底から継承した positional property)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Records_NullableStructFieldsCompareByValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; }
            public record Opt(S? value);
            public record struct OptStruct(S? value, int k);
            public record E;
            public record E2 : E;
            public class P
            {
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    var one = new S { N = 1 };
                    var two = new S { N = 2 };
                    Console.WriteLine("record:" + B(new Opt(one) == new Opt(one)) + ":" + B(new Opt(one) == new Opt(two))
                        + ":" + B(new Opt(null) == new Opt(null)) + ":" + B(new Opt(one) == new Opt(null)) + ":" + B(new Opt(null) == new Opt(one)));
                    Console.WriteLine("record-struct:" + B(new OptStruct(one, 1) == new OptStruct(one, 1)) + ":" + B(new OptStruct(one, 1) == new OptStruct(two, 1))
                        + ":" + B(new OptStruct(null, 1) == new OptStruct(null, 1)) + ":" + B(new OptStruct(null, 1) == new OptStruct(one, 1)));
                    E e = new E();
                    Console.WriteLine("no-field:" + B(new E() == new E()) + ":" + B(e == new E2()) + ":" + B(new E2() == new E2()));
                }
            }
            """, "P");
        Assert.Equal("""
            record:1:0:1:0:0
            record-struct:1:0:1:0
            no-field:1:0:1
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Records_DerivedIncludesBaseFields()
    {
        var output = Backends.AssertParity("""
            using System;
            public record Root(int tag);
            public record D(int v) : Root(P.Next());
            public record Pass(int tag) : Root(tag * 2);
            public record Mid(int tag, int m) : Root(tag);
            public record Leaf(int tag, int m, int k) : Mid(tag, m);
            public record WithMethod(int a) { public int Get() { return a + 100; } }
            public record DerivedMethod(int a, int b) : WithMethod(a + 1);
            public class P
            {
                static int counter;
                public static int Next() { counter++; return counter; }
                public static int B(bool b) => b ? 1 : 0;
                public static void Main()
                {
                    Console.WriteLine("base-ctor:" + B(new D(1) == new D(1)) + ":" + counter);
                    var a = new D(1) { tag = 10 };
                    var b = new D(1) { tag = 20 };
                    var c = new D(1) { tag = 10 };
                    Console.WriteLine("base-field:" + a.tag + ":" + b.tag + ":" + B(a == b) + ":" + B(a == c) + ":" + B(a != b));
                    var pass = new Pass(3);
                    Console.WriteLine("pass-through:" + pass.tag + ":" + B(pass == new Pass(3)) + ":" + B(pass == new Pass(4)));
                    Console.WriteLine("chain:" + B(new Leaf(1, 2, 3) == new Leaf(1, 2, 3)) + ":" + B(new Leaf(1, 2, 3) == new Leaf(1, 9, 3))
                        + ":" + B(new Leaf(1, 2, 3) == new Leaf(9, 2, 3)) + ":" + new Leaf(1, 2, 3).m);
                    var dm = new DerivedMethod(1, 2);
                    WithMethod asBase = dm;
                    Console.WriteLine("inherited:" + dm.a + ":" + dm.Get() + ":" + asBase.Get()
                        + ":" + B(dm == new DerivedMethod(1, 2)) + ":" + B(dm == new DerivedMethod(1, 3)));
                }
            }
            """, "P");
        Assert.Equal("""
            base-ctor:0:2
            base-field:10:20:0:1:1
            pass-through:6:1:0
            chain:1:0:0:2
            inherited:2:102:102:1:0
            """.ReplaceLineEndings("\n"), output);
    }
}
