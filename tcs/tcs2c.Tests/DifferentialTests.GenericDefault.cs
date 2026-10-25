namespace TinyCs.Tcs2c.Tests;

// 型引数の既定値 (`default(T)`、`default`、generic class の initializer の無い field)。
// Lua は型を消すので、呼び出し側が型引数ごとの既定値を渡す。型引数を受け渡す
// generic method、lambda、generic class の instance / static method を含む。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void GenericDefault_IsDefaultOfTypeArgument()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public readonly record struct V(int A);
            public struct M { public int N; }
            public enum E { A, B }
            public class Box<T>
            {
                public T Value;
                public T Other = default!;
                public T Get() => Value;
                public T Fresh() { T t = default!; return t; }
                public U Map<U>(Func<T, U> f) => f(Value);
                public U Empty<U>() => default!;
                public static T Zero() => default!;
                public T ViaStatic() => Zero();
            }
            public static class G
            {
                public static T Pick<T>(List<T> list, int i) => i < list.Count ? list[i] : default!;
                public static T Forward<T>(List<T> list) => Pick(list, 99);
                public static Func<T> Later<T>() => () => default!;
                public static U Second<T, U>(T t) => default!;
                public static T OrDefault<T>(this List<T> list, int i) => Pick(list, i);
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.Pick(new List<int> { 1 }, 5) + 1);
                    Console.WriteLine(G.Pick(new List<float> { 1 }, 5) + 0.5f);
                    Console.WriteLine(G.Pick(new List<bool> { true }, 5) ? "T" : "F");
                    Console.WriteLine(G.Pick(new List<string> { "a" }, 5) == null ? "null" : "set");
                    Console.WriteLine(G.Pick(new List<V> { new V(3) }, 5).A);
                    Console.WriteLine(G.Pick(new List<M> { new M() }, 5).N);
                    Console.WriteLine(G.Pick(new List<int?> { 1 }, 5) == null ? "null" : "set");
                    Console.WriteLine(G.Pick(new List<E> { E.B }, 5));
                    Console.WriteLine(G.Forward(new List<int>()) + 2);
                    Console.WriteLine(G.Later<int>()() + 3);
                    Console.WriteLine(G.Second<string, int>("x") + 4);
                    Console.WriteLine(new List<int>().OrDefault(0) + 5);
                    Console.WriteLine(G.OrDefault(new List<int>(), 0) + 6);
                    var b = new Box<int>();
                    Console.WriteLine(b.Value + b.Other + b.Get() + b.Fresh() + b.ViaStatic() + 8);
                    Console.WriteLine(b.Map(x => x + 9) + b.Empty<int>());
                    var bv = new Box<V>();
                    Console.WriteLine(bv.Value.A + bv.Get().A + bv.Fresh().A);
                    var bs = new Box<string>();
                    Console.WriteLine(bs.Value == null ? "null" : "set");
                }
            }
            """, "P");
        Assert.Equal("""
            1
            0.5
            F
            null
            0
            0
            null
            A
            2
            3
            4
            5
            6
            8
            9
            0
            null
            """, output);
    }

    // 省略した `T x = default` は callee の型引数の既定値 (generic な呼び出し元の型引数ではない)。
    // generic な struct の配列の要素と、generic な型の operator の中の既定値
    [CFact]
    public void GenericDefault_OmittedArgumentArrayAndOperator()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct M { public int N; }
            public struct S<T> { public T V; }
            public class Vec<T>
            {
                public T X = default!;
                public static Vec<T> operator +(Vec<T> a, int b) => new Vec<T>();
                public static Vec<T> operator -(Vec<T> a, int b) { T t = default!; var v = new Vec<T>(); v.X = t; return v; }
                public static Vec<T> operator *(Vec<T>? a, Vec<T>? b) { T t = default!; var v = new Vec<T>(); v.X = t; return v; }
            }
            public static class G
            {
                public static T Get<T>(T x = default!) => x;
                public static int Outer<T>() => Get<int>() + 1;
                public static string Outer2<T>() => Get<string>() == null ? "null" : "set";
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.Get<int>() + "|" + G.Outer<M>() + "|" + G.Outer2<M>());
                    var arr = new S<int>[2];
                    arr[0].V = 3;
                    var ms = new S<M>[1];
                    Console.WriteLine((arr[0].V + arr[1].V) + "|" + ms[0].V.N);
                    var v = new Vec<int>() + 2;
                    var w = new Vec<int>() - 2;
                    Vec<int>? none = null;
                    var z = none * none;
                    z += 1;
                    Console.WriteLine((v.X + 1) + "|" + (w.X + 2) + "|" + (z.X + 3));
                }
            }
            """, "P");
        Assert.Equal("""
            0|1|null
            3|0
            1|2|3
            """, output);
    }

    // ctor の省略した引数は宣言の既定値 (record / record struct の positional parameter、
    // struct と class の ctor、generic な record の `T b = default`)
    [CFact]
    public void OmittedConstructorArguments_UseDeclaredDefaults()
    {
        var output = Backends.AssertParity("""
            using System;
            public class C { public int A; public int B; public C(int a, int b = 7) { A = a; B = b; } }
            public record R(int A, int B = 1);
            public record struct RS(int A, int B = 2);
            public readonly record struct RR(int A, string S = "s");
            public struct S { public int A; public int B; public S(int a, int b = 3) { A = a; B = b; } }
            public record G<T>(T A, T B = default!);
            public static class P
            {
                public static void Main()
                {
                    var c = new C(1);
                    var r = new R(5);
                    var rs = new RS(6);
                    var rr = new RR(7);
                    var s = new S(8);
                    C c2 = new(1);
                    var g = new G<int>(4);
                    Console.WriteLine(c.B + ":" + r.B + ":" + rs.B + ":" + rr.S + ":" + s.B + ":" + c2.B + ":" + (g.B + 1));
                }
            }
            """, "P");
        Assert.Equal("7:1:2:s:3:7:1", output);
    }
}
