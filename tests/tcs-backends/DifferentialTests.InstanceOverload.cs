namespace TinyCs.Tcs2c.Tests;

// Instance method overloads. A method that implements an interface method gets the same name as the interface method,
// and calls through the interface and through the class select the same method.
// Also includes types with a static and an instance method of the same name, and struct instance methods.
// The expected values are the stdout of running the same program on CoreCLR
public class InstanceOverloadDifferentialTests
{
    [CFact]
    public void InstanceOverload_DispatchesBySignature()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface IShape
            {
                string Area();
                string Area(int scale);
            }
            public interface INamed { string Name(); }
            public class Square : IShape, INamed
            {
                public int Side = 3;
                public string Area() => "area " + Side * Side;
                public string Area(int scale) => "scaled " + Side * Side * scale;
                public string Area(float scale) => "float " + scale;
                public string Name() => "square";
                public string Name(string prefix) => prefix + "square";
                public static string Name(int n) => "static " + n;
            }
            public struct Counter
            {
                public int N;
                public void Add() { N += 1; }
                public void Add(int k) { N += k; }
            }
            public static class P
            {
                public static void Main()
                {
                    var sq = new Square();
                    IShape shape = sq;
                    INamed named = sq;
                    Console.WriteLine(sq.Area() + "|" + sq.Area(2) + "|" + sq.Area(1.5f));
                    Console.WriteLine(shape.Area() + "|" + shape.Area(3));
                    Console.WriteLine(named.Name() + "|" + sq.Name("a ") + "|" + Square.Name(4));
                    var c = new Counter();
                    c.Add();
                    c.Add(5);
                    Console.WriteLine(c.N);
                }
            }
            """, "P");
        Assert.Equal("""
            area 9|scaled 18|float 1.5
            area 9|scaled 27
            square|a square|static 4
            6
            """, output);
    }

    [CFact]
    public void InstanceOverload_ConditionalCallUsesBoundOverload()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            public class C
            {
                public string F(int x) => "int " + x;
                public string F(string s) => "str " + s;
                public string G() => "g0";
                public string G(int x) => "g1 " + x;
            }
            public struct S
            {
                public int N;
                public int F(int x) => N + x;
                public int F(string s) => N + s.RuneCount();
            }
            public static class P
            {
                public static void Main()
                {
                    C? c = new C();
                    Console.WriteLine(c?.F("a") + "|" + c?.F(1) + "|" + c?.G(5) + "|" + c?.G());
                    S? s = new S { N = 10 };
                    Console.WriteLine(s?.F("xyz") + "|" + s?.F(2));
                }
            }
            """, "P");
        Assert.Equal("""
            str a|int 1|g1 5|g0
            13|12
            """, output);
    }

    // Overloads whose parameter types overlap once type arguments are substituted (T = string for `M(T)` and `M(string)`).
    // C# selects by the declared types (`M("x")` is `M(string)`, through T it is `M(T)`)
    [CFact]
    public void InstanceOverload_InGenericTypeWithCoincidingTypeArgument()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface IPick<T>
            {
                string Pick(T x);
                string Pick(string s);
            }
            public class G<T> : IPick<T>
            {
                public string Pick(T x) => "T";
                public string Pick(string s) => "string";
                public string Via(T x) => Pick(x);
            }
            public static class P
            {
                public static void Main()
                {
                    var g = new G<string>();
                    IPick<string> i = g;
                    Console.WriteLine(g.Pick("a") + "|" + g.Via("b") + "|" + i.Pick("c") + "|" + g?.Pick("d"));
                    var n = new G<int>();
                    Console.WriteLine(n.Pick(1) + "|" + n.Pick("e"));
                }
            }
            """, "P");
        Assert.Equal("""
            string|T|string|string
            T|string
            """, output);
    }

    // Generic methods of interfaces. C creates closed methods with the same name on the interface and the implementing types
    // (including when a closed copy of a generic type implements it, and generic interfaces)
    [CFact]
    public void InterfaceGenericMethod_DispatchesThroughInterface()
    {
        var output = Backends.AssertParity("""
            using System;
            public interface I { T Get<T>(T fallback); }
            public class C : I { public T Get<T>(T fallback) => fallback; }
            public class D<V> : I { public V Seed = default!; public T Get<T>(T fallback) => default!; }
            public interface IConv<T> { U Do<U>(T x, U u); }
            public class Conv<T> : IConv<T> { public U Do<U>(T x, U u) => u; }
            public static class P
            {
                public static void Main()
                {
                    I i = new C();
                    I d = new D<string>();
                    var c = new C();
                    IConv<int> conv = new Conv<int>();
                    Console.WriteLine(i.Get<int>(3) + "|" + i.Get<string>("s") + "|" + d.Get<int>(5) + "|" + c.Get<int>(7)
                        + "|" + conv.Do<string>(1, "u"));
                }
            }
            """, "P");
        Assert.Equal("3|s|0|7|u", output);
    }

    // Overloads of generic methods called with explicit type arguments inside a generic type (U cannot be inferred)
    [CFact]
    public void InstanceOverload_GenericMethodsInGenericTypeWithExplicitTypeArguments()
    {
        var output = Backends.AssertParity("""
            using System;
            public class G<T>
            {
                public U Make<U>(T t) => default!;
                public U Make<U>(string s) => default!;
                public string Twice<U>(U u) => "" + u + u;
            #pragma warning disable CS0693
                public string Shadow<T>(T x) => "" + x;
            #pragma warning restore CS0693
            }
            public static class P
            {
                public static void Main()
                {
                    var g = new G<int>();
                    Console.WriteLine(g.Make<int>(1) + "|" + (g.Make<string>("a") == null ? "null" : "x") + "|" + g.Twice<int>(4) + "|" + g.Shadow<string>("s"));
                }
            }
            """, "P");
        Assert.Equal("0|null|44|s", output);
    }
}
