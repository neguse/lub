namespace TinyCs.Tcs2c.Tests;

// lambda の中の lambda が外側の lambda の parameter (と外側が捕捉した変数) を
// 捕捉する。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Closures_NestedCaptureOuterLambdaParameter()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Counter
            {
                public int Base = 10;
                public int Run(List<int> xs)
                {
                    Func<int, int> f = k => xs.Count(y => y + Base > k);
                    return f(12);
                }
            }
            public class P
            {
                static List<int> Values = new List<int> { 1, 2, 2, 3 };
                static Func<int, int> Field = x => Values.Count(y => y == x);
                public static void Main()
                {
                    var arr = new List<int> { 1, 2, 2, 3 };
                    Func<int, int> g = x => arr.Count(y => y == x);
                    Console.WriteLine(g(2) + ":" + g(5));
                    Func<int, int> h = x => { Func<int> inc = () => { x++; return x; }; inc(); return x + inc(); };
                    Console.WriteLine(h(2));
                    Func<int, Func<int, Func<int, int>>> add3 = a => b => c => a * 100 + b * 10 + c;
                    Console.WriteLine(add3(1)(2)(3));
                    var counts = arr.Select(x => arr.Where(y => y > x).Count()).ToList();
                    Console.WriteLine(counts[0] + "," + counts[1] + "," + counts[2] + "," + counts[3]);
                    Console.WriteLine(Field(2) + ":" + new Counter().Run(arr));
                }
            }
            """, "P");
        Assert.Equal("""
            2:0
            7
            123
            3,1,1,0
            2:1
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Closures_NestedCaptureSurvivesFrames()
    {
        // 外側の lambda の parameter を捕捉した closure を static の List に
        // 残し、フレーム境界の GC (stress 含む) をまたいで呼ぶ
        const string source = """
            using System;
            using System.Collections.Generic;
            public class Program
            {
                public static List<Func<string>> Kept = new List<Func<string>>();
                public static Func<string, Func<int, Func<string>>> Make;
                public static int Frames;
                public static void Setup()
                {
                    Kept = new List<Func<string>>();
                    Frames = 0;
                    Make = name => n => () => name + ":" + (n * 2);
                }
                public static void Frame()
                {
                    var tag = "f" + Frames;
                    Kept.Add(Make(tag)(Frames));
                    var junk = new List<string>();
                    for (int i = 0; i < 20; i++) junk.Add("x" + i);
                    Frames = Frames + 1;
                }
                public static void Report()
                {
                    var all = "";
                    foreach (var f in Kept) all = all + f() + ",";
                    Console.WriteLine(all);
                }
            }
            """;
        Assert.Equal("f0:0,f1:2,f2:4,f3:6,f4:8,f5:10,",
            Backends.AssertParityLib(source, "Program", 6));
    }
}
