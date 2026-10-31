namespace TinyCs.Tcs2c.Tests;

// ToString の override (class / record / struct / record struct / generic)。連結 / 補間 / Console.WriteLine は
// 静的に解決して直接呼ぶ。C# と同じく null の値と ToString が返した null は空文字列、struct の文字列化は copy の上で
// 呼ぶので書き換えは変数に残らない。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void ToStringOverride_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class C
            {
                public int X;
                public C(int x) { X = x; }
                public override string ToString() { return "C(" + X + ")"; }
                public string Inner() { return ToString() + this.ToString(); }
            }
            public class N { public override string ToString() => null; }
            public record R(int A) { public override string ToString() => "R" + A; }
            public struct S
            {
                public int X;
                public int Calls;
                public override string ToString() { Calls++; return "S(" + X + "," + Calls + ")"; }
                public string Inner() { return ToString(); }
            }
            public readonly struct RO
            {
                public readonly int X;
                public RO(int x) { X = x; }
                public override string ToString() => "RO" + X;
            }
            public record struct RS(int A) { public override string ToString() => "RS" + A; }
            public struct Plain { public int X; public string ToString(int n) { return "p" + n; } }
            public struct Gen<T>
            {
                public T V;
                public override string ToString() { return "Gen:" + V; }
            }
            public enum E { A, B }
            public static class G
            {
                public static string Show<T>(T v) { return v.ToString(); }
                public static string Cat<T>(T v) { return "<" + v + ">"; }
            }
            public class Holder { public S F; public S[] Arr = new S[1]; public C Ref; public static S St; public S Prop { get; set; } }
            public static class P
            {
                public static void Main()
                {
                    var c = new C(1);
                    Console.WriteLine(c.ToString());
                    Console.WriteLine(c);
                    Console.WriteLine("a" + c + "b");
                    Console.WriteLine($"[{c}] [{c,6}] [{c:F2}]");
                    Console.WriteLine(c.Inner());
                    C nc = null;
                    Console.WriteLine("n:" + nc + ":" + $"{nc}" + ":");
                    Console.WriteLine(nc);
                    Console.WriteLine(nc?.ToString() == null);
                    Console.WriteLine("nv:" + new N() + ":" + $"{new N()}" + ":");
                    Console.WriteLine(new N().ToString() == null);
                    var text = "t";
                    text += c;
                    Console.WriteLine(text);
                    Console.WriteLine(new R(2).ToString() + new R(3) + $"{new R(4)}");

                    var s = new S { X = 5 };
                    Console.WriteLine(s.ToString());
                    Console.WriteLine(s.ToString());
                    Console.WriteLine(s.Calls);
                    Console.WriteLine(s);
                    Console.WriteLine("a" + s);
                    Console.WriteLine($"{s}");
                    Console.WriteLine(s.Calls);
                    Console.WriteLine(s.Inner() + " " + s.Calls);
                    Console.WriteLine(new RO(6).ToString() + new RO(7) + $"{new RO(8)}");
                    Console.WriteLine(new RS(9).ToString() + new RS(10) + $"{new RS(11)}");
                    Console.WriteLine(new Plain().ToString(3));
                    var h = new Holder();
                    h.F.X = 1;
                    h.Arr[0].X = 2;
                    Holder.St.X = 3;
                    Console.WriteLine(h.F.ToString() + h.Arr[0].ToString() + Holder.St.ToString() + h.Prop.ToString());
                    Console.WriteLine(h.F.Calls + " " + h.Arr[0].Calls + " " + Holder.St.Calls + " " + h.Prop.Calls);
                    Console.WriteLine("" + h.F + h.Arr[0] + Holder.St);
                    Console.WriteLine(h.F.Calls + " " + h.Arr[0].Calls + " " + Holder.St.Calls);
                    h.Ref = new C(8);
                    Console.WriteLine(h.Ref?.ToString());
                    S? ns = new S { X = 4 };
                    Console.WriteLine(ns?.ToString());
                    S? none = null;
                    Console.WriteLine(none?.ToString() == null);
                    Console.WriteLine(new Gen<int> { V = 3 }.ToString() + new Gen<string> { V = "x" });
                    Console.WriteLine(G.Show(c) + G.Cat(c) + G.Show(new R(1)) + G.Cat(new R(1)));
                    Console.WriteLine(G.Show(new S { X = 2 }) + G.Cat(new S { X = 2 }));
                    Console.WriteLine(G.Show(new RO(2)) + G.Cat(new RO(2)) + G.Show(new RS(2)));
                    Console.WriteLine(G.Show(1) + G.Cat("s") + G.Show(E.B));
                }
            }
            """, "P");
        Assert.Equal("""
            C(1)
            C(1)
            aC(1)b
            [C(1)] [  C(1)] [C(1)]
            C(1)C(1)
            n:::

            True
            nv:::
            True
            tC(1)
            R2R3R4
            S(5,1)
            S(5,2)
            2
            S(5,3)
            aS(5,3)
            S(5,3)
            2
            S(5,3) 3
            RO6RO7RO8
            RS9RS10RS11
            p3
            S(1,1)S(2,1)S(3,1)S(0,1)
            1 1 1 0
            S(1,2)S(2,2)S(3,2)
            1 1 1
            C(8)
            S(4,1)
            True
            Gen:3Gen:x
            C(1)<C(1)>R1<R1>
            S(2,1)<S(2,1)>
            RO2<RO2>RS2
            1<s>B
            """.ReplaceLineEndings("\n"), output);
    }

    // `new T()` の値の ToString は、呼ぶたびに同じ変数を書き換える (C# 仕様 structs の例)
    [CFact]
    public void ToStringOverride_OnNewTypeArgumentMutatesTheVariable()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct Counter
            {
                int value;
                public override string ToString() { value++; return value.ToString(); }
            }
            public static class P
            {
                static void Test<T>() where T : new()
                {
                    T x = new T();
                    Console.WriteLine(x.ToString());
                    Console.WriteLine(x.ToString());
                }
                public static void Main() { Test<Counter>(); }
            }
            """, "P");
        Assert.Equal("1\n2", output);
    }

    // Equals / GetHashCode の override は record / record struct で書く。sealed は継承の語
    [Theory]
    [InlineData("public class T { public override bool Equals(object o) => true; }")]
    [InlineData("public class T { public override int GetHashCode() => 1; }")]
    [InlineData("public struct T { public override bool Equals(object o) => true; }")]
    [InlineData("public struct T { public override int GetHashCode() => 1; }")]
    [InlineData("public record struct T(int A) { public override int GetHashCode() => 1; }")]
    [InlineData("public class T { public sealed override string ToString() => \"\"; }")]
    public void OverrideOtherThanToString_BothBackendsStopWithDiagnostic(string declaration)
    {
        var program = declaration + "\npublic static class P { public static void Main() { } }";
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("error TCS1001: unsupported syntax: OverrideMember", ex.Message);
        }
    }

    // string.Join の要素は runtime が文字列にするので override を呼べない。S? の値は null の扱いが
    // 値で決まるので連結 / 補間 / WriteLine に出さない (`?.ToString()` は書ける)
    [Theory]
    [InlineData("Console.WriteLine(string.Join(\",\", new List<C> { new C() }));", "UnsupportedToString(C)")]
    [InlineData("Console.WriteLine(string.Join(\",\", new S[] { new S() }));", "UnsupportedToString(S)")]
    [InlineData("S? n = new S(); Console.WriteLine(\"x\" + n);", "StructBoxing")]
    [InlineData("S? n = new S(); Console.WriteLine(n);", "StructBoxing")]
    [InlineData("object o = new S();", "StructBoxing")]
    public void ToStringOverrideOutsideDirectCalls_BothBackendsStopWithDiagnostic(string statement, string rule)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            public class C { public override string ToString() => "c"; }
            public struct S { public override string ToString() => "s"; }
            public static class P { public static void Main() { {{statement}} } }
            """;
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }
}
