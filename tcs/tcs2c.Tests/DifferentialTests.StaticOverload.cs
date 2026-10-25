namespace TinyCs.Tcs2c.Tests;

// static method の overload (引数の型と数、generic の拡張 method、method group、
// 拡張 method の static の形)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void StaticOverload_DispatchesBySignature()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public readonly record struct V(int A, float B);
            public static class Ops
            {
                public static string Show() { return "none"; }
                public static string Show(int x) { return "int " + x; }
                public static string Show(float x) { return "float " + x; }
                public static string Show(string s) { return "string " + s; }
                public static string Show(int x, int y) { return "pair " + (x + y); }
                public static string Show(V v) { return "V " + v.A; }
                public static string Show(int[] xs) { return "array " + xs.Length; }
            }
            public static class Agg
            {
                public static int Total<T>(this List<T> list, Func<T, int> f)
                {
                    int s = 0;
                    for (int i = 0; i < list.Count; i++) s += f(list[i]);
                    return s;
                }
                public static float Total<T>(this List<T> list, Func<T, float> f)
                {
                    float s = 0;
                    for (int i = 0; i < list.Count; i++) s += f(list[i]);
                    return s;
                }
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(Ops.Show());
                    Console.WriteLine(Ops.Show(3));
                    Console.WriteLine(Ops.Show(1.5f));
                    Console.WriteLine(Ops.Show("s"));
                    Console.WriteLine(Ops.Show(2, 5));
                    Console.WriteLine(Ops.Show(new V(7, 0f)));
                    Console.WriteLine(Ops.Show(new int[] { 1, 2 }));
                    var vs = new List<V> { new V(1, 0.5f), new V(2, 0.25f) };
                    Console.WriteLine(vs.Total(v => v.A));
                    Console.WriteLine(vs.Total(v => v.B));
                    Console.WriteLine(Agg.Total(vs, v => v.A * 10));
                    var ints = new List<int> { 4, 5 };
                    Console.WriteLine(ints.Total(x => x));
                    Func<int, string> f = Ops.Show;
                    Func<string, string> g = Ops.Show;
                    Func<string> h = Ops.Show;
                    Console.WriteLine(f(9) + "," + g("t") + "," + h());
                }
            }
            """, "P");
        Assert.Equal("""
            none
            int 3
            float 1.5
            string s
            pair 7
            V 7
            array 2
            3
            0.75
            30
            9
            int 9,string t,none
            """, output);
    }
}
