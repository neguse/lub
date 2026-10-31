namespace TinyCs.Tcs2c.Tests;

// 複数の constructor (class / struct / record struct / generic)。呼び出しは Roslyn の選んだ
// overload を直接呼ぶ。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void MultipleConstructors_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class C
            {
                public int X = 5;
                public string Name = "init";
                public List<int> Items = new List<int>();
                public static int Made;
                public C() { Made++; }
                public C(int x) { X = x; Made += 10; }
                public C(string name) { Name = name; }
                public C(int x, string name) { X = x; Name = name; Items.Add(x); }
                public C(C other) { X = other.X + 1; Name = other.Name; }
                public C(float f, int scale = 3) { X = (int)f * scale; }
                public string Show() { return X + ":" + Name + ":" + Items.Count; }
            }
            public struct V
            {
                public float X, Y;
                public int Tag = 7;
                public V(float x) { X = x; Y = 0f; }
                public V(float x, float y) { X = x; Y = y; Tag = 1; }
                public V(V other) { X = other.X; Y = other.Y; other.X = 100f; Tag = 2; }
                public string Show() { return X + "," + Y + "," + Tag; }
            }
            public record struct RS
            {
                public int A;
                public RS(int a) { A = a; }
                public RS(string s) { A = 1; }
            }
            public class Box<T>
            {
                public T Value;
                public int Size;
                public Box(T value) { Value = value; Size = 1; }
                public Box(T value, int size) { Value = value; Size = size; }
            }
            public struct Pair<T>
            {
                public T A, B;
                public Pair(T a) { A = a; B = a; }
                public Pair(T a, T b) { A = a; B = b; }
            }
            public class Init
            {
                public int P;
                public int Q;
                public Init() { P = 1; }
                public Init(int p) { P = p; }
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(new C().Show() + " " + C.Made);
                    Console.WriteLine(new C(1).Show() + " " + C.Made);
                    Console.WriteLine(new C("n").Show());
                    Console.WriteLine(new C(2, "m").Show());
                    Console.WriteLine(new C(new C(4)).Show());
                    Console.WriteLine(new C(2f).Show() + " " + new C(2f, 5).Show());
                    C implicitNew = new(9);
                    Console.WriteLine(implicitNew.Show());
                    var a = new V(1f);
                    var b = new V(1f, 2f);
                    var c = new V(b);
                    var d = new V();
                    Console.WriteLine(a.Show() + " " + b.Show() + " " + c.Show() + " " + d.Show());
                    Console.WriteLine(new RS(3).A + " " + new RS("x").A + " " + new RS().A);
                    Console.WriteLine(new Box<int>(4).Size + " " + new Box<int>(4, 9).Size + " " + new Box<string>("s", 2).Value);
                    Console.WriteLine(new Pair<int>(4).B + " " + new Pair<int>(4, 5).B + " " + new Pair<string>("q").B);
                    var i = new Init(3) { Q = 4 };
                    var j = new Init { Q = 5 };
                    Console.WriteLine(i.P + " " + i.Q + " " + j.P + " " + j.Q);
                    var list = new List<V> { new V(2f), new V(2f, 3f) };
                    Console.WriteLine(list[0].Show() + " " + list[1].Show());
                    Func<int, C> make = n => new C(n);
                    Console.WriteLine(make(6).X);
                }
            }
            """, "P");
        Assert.Equal("""
            5:init:0 1
            1:init:0 11
            5:n:0
            2:m:1
            5:init:0
            6:init:0 10:init:0
            9:init:0
            1,0,7 1,2,1 1,2,2 0,0,0
            3 1 0
            1 9 s
            4 5 q
            3 4 1 5
            2,0,7 2,3,1
            6
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void ThisConstructorInitializer_BothBackendsStopWithDiagnostic()
    {
        const string program = """
            public class C
            {
                public int X;
                public C() : this(1) { }
                public C(int x) { X = x; }
            }
            public static class P { public static void Main() { } }
            """;
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains("error TCS1001: unsupported syntax: ThisConstructorInitializer", ex.Message);
        }
    }
}
