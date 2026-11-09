namespace TinyCs.Tcs2c.Tests;

// Dictionary への store (代入 / 複合代入 / Add / initializer) は key を値より
// 先に評価する (C# の順序)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void DictStore_EvaluatesKeyBeforeValue()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                static int I;
                static string Log = "";
                static int Right() { I = 1; return 7; }
                static int K(int k) { Log += "k"; return k; }
                static int V(int v) { Log += "v"; return v; }
                static string SK() { Log += "k"; return "a"; }
                public static void Main()
                {
                    var d = new Dictionary<int, int> { [0] = 10, [1] = 20 };
                    I = 0;
                    d[I] += Right();
                    Console.WriteLine(d[0] + "," + d[1]);
                    I = 0;
                    d[I] = Right();
                    Console.WriteLine(d[0] + "," + d[1]);
                    I = 0;
                    d[I] -= Right();
                    Console.WriteLine(d[0] + "," + d[1]);
                    var e = new Dictionary<int, int> { [K(1)] = V(2) };
                    e[K(3)] = V(4);
                    e.Add(K(5), V(6));
                    var s = new Dictionary<string, int>();
                    s[SK()] = V(1);
                    s[SK()] += V(2);
                    Console.WriteLine(Log + ":" + e[1] + e[3] + e[5] + s["a"]);
                }
            }
            """, "P");
        Assert.Equal("17,20\n7,20\n0,20\nkvkvkvkvkv:2463", output);
    }
}
