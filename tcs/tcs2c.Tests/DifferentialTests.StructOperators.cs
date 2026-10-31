namespace TinyCs.Tcs2c.Tests;

// struct の演算子 (class と同じ範囲) と static member。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private const string StructOperatorsProgram = """
        using System;
        using System.Collections.Generic;
        public struct V
        {
            public float X, Y;
            public V(float x, float y) { X = x; Y = y; }
            public static V operator +(V a, V b) => new V(a.X + b.X, a.Y + b.Y);
            public static V operator -(V a, V b) { return new V(a.X - b.X, a.Y - b.Y); }
            public static V operator -(V a) => new V(-a.X, -a.Y);
            public static V operator *(V a, float s) => new V(a.X * s, a.Y * s);
            public static V operator *(float s, V a) => new V(a.X * s, a.Y * s);
            public static V operator /(V a, float s) => new V(a.X / s, a.Y / s);
            public static V operator %(V a, float s) => new V(a.X % s, a.Y % s);
            public string Show() { return X + "," + Y; }
        }
        public readonly record struct R(int A)
        {
            public static R operator +(R a, R b) => new R(a.A + b.A);
            public static readonly R One = new R(1);
            public static R Twice(R r) { return r + r; }
        }
        public struct M
        {
            public int N;
            // operand は値渡しで、中の書き換えは呼び出し側に見えない
            public static M operator +(M a, M b) { a.N += b.N; b.N = 100; return a; }
        }
        public class Holder { public V P; public V[] Items = new V[2]; }
        public static class P
        {
            public static void Main()
            {
                var a = new V(1f, 2f);
                var b = new V(3f, 5f);
                Console.WriteLine((a + b).Show());
                Console.WriteLine((b - a).Show());
                Console.WriteLine((-a).Show());
                Console.WriteLine((a * 2f).Show());
                Console.WriteLine((2f * a).Show());
                Console.WriteLine((b / 2f).Show());
                Console.WriteLine((b % 2f).Show());
                var c = a;
                c += b;
                c *= 2f;
                Console.WriteLine(c.Show() + " " + a.Show());
                var h = new Holder();
                h.P += a;
                h.P += a;
                h.Items[1] += b;
                h.Items[1] -= a;
                Console.WriteLine(h.P.Show() + " " + h.Items[1].Show() + " " + h.Items[0].Show());
                var m = new M { N = 1 };
                var n = new M { N = 2 };
                var o = m + n;
                Console.WriteLine(m.N + " " + n.N + " " + o.N);
                Console.WriteLine((new R(2) + R.One).A + " " + R.Twice(new R(3)).A);
                Func<V, V, V> add = (x, y) => x + y;
                Console.WriteLine(add(a, b).Show());
                var list = new List<V> { a, b };
                var sum = new V();
                foreach (var v in list) sum += v;
                Console.WriteLine(sum.Show());
            }
        }
        """;

    [CFact]
    public void StructOperators_MatchDotnet()
    {
        var output = Backends.AssertParity(StructOperatorsProgram, "P");
        Assert.Equal("""
            4,7
            2,3
            -1,-2
            2,4
            2,4
            1.5,2.5
            1,1
            8,14 1,2
            2,4 2,3 0,0
            1 2 3
            3 6
            4,7
            4,7
            """.ReplaceLineEndings("\n"), output);
    }

    private const string StructStaticsProgram = """
        using System;
        using System.Collections.Generic;
        public struct V
        {
            public float X;
            public const int K = 7;
            public static int Made;
            public static readonly V Unit = Make(1f);
            public static V Last;
            public static int Total { get; set; } = 40;
            public static string Label { get; } = "v" + K;
            public static int Doubled => Total * 2;
            public static int Stored { get { return Made * 10; } set { Made = value / 10; } }
            public static V Make(float x) { var v = new V(); v.X = x; Made++; Last = v; return v; }
            public static float Sum(V a, V b) { a.X += b.X; return a.X; }
            public float Scaled() { return X * Scale(2f); }
            private static float Scale(float s) { return s + Made; }
        }
        public record struct RS(int A)
        {
            public static int Count = Dep.Base + 1;
            public static RS Of(int a) { Count++; return new RS(a); }
        }
        public static class Dep { public static int Base = 10; }
        public struct Box<T>
        {
            public T Value;
            public static Box<T> Of(T value) { var b = new Box<T>(); b.Value = value; return b; }
            public static Box<T> Empty() { return new Box<T>(); }
            public static T DefaultValue => default(T);
        }
        public class Owner { public static V Shared = new V { X = 5f }; public List<string> Names = new List<string>(); }
        public struct Rooted
        {
            public static Owner Keep = new Owner();
            public static List<string> Log = new List<string>();
            public static void Add(string s) { Log.Add(s + s); Keep.Names.Add(s); }
        }
        public static class P
        {
            public static float Apply(Func<V, V, float> f, V a, V b) { return f(a, b); }
            public static void Main()
            {
                Console.WriteLine(V.K + " " + V.Label + " " + V.Total + " " + V.Doubled);
                V.Total += 2;
                Console.WriteLine(V.Total + " " + V.Doubled);
                Console.WriteLine(V.Unit.X + " " + V.Made + " " + V.Last.X);
                var a = V.Make(3f);
                Console.WriteLine(a.X + " " + V.Made + " " + V.Stored + " " + a.Scaled());
                V.Stored = 50;
                Console.WriteLine(V.Made);
                var b = a;
                b.X = 9f;
                Console.WriteLine(V.Sum(a, b) + " " + a.X + " " + b.X);
                Console.WriteLine(Apply(V.Sum, a, b));
                // static の struct を受ける変数は copy で、static の値は変わらない
                var u = V.Last;
                u.X = 99f;
                Console.WriteLine(V.Last.X);
                V.Last.X = 8f;
                Console.WriteLine(V.Last.X + " " + Owner.Shared.X);
                Console.WriteLine(RS.Count + " " + RS.Of(4).A + " " + RS.Count);
                Console.WriteLine(Box<int>.Of(4).Value + " " + Box<int>.Empty().Value + " " + Box<int>.DefaultValue);
                Console.WriteLine(Box<string>.Of("s").Value + " " + (Box<string>.Empty().Value == null));
                for (int i = 0; i < 3; i++) Rooted.Add("r" + i);
                Console.WriteLine(string.Join(",", Rooted.Log) + " " + Rooted.Keep.Names.Count);
            }
        }
        """;

    [CFact]
    public void StructStatics_MatchDotnet()
    {
        var output = Backends.AssertParity(StructStaticsProgram, "P");
        Assert.Equal("""
            7 v7 40 80
            42 84
            1 1 1
            3 2 20 12
            5
            12 3 9
            12
            3
            8 5
            11 4 12
            4 0 0
            s True
            r0r0,r1r1,r2r2 3
            """.ReplaceLineEndings("\n"), output);
    }
}
