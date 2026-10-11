namespace TinyCs.Tcs2c.Tests;

// int / float の CompareTo (-1 / 0 / 1。float の NaN は他のどの値より小さく、
// NaN 同士は 0) と、それを比較に使う struct の List.Sort。期待値は同じ
// program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Numbers_CompareToAndStructListSort()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct S { public int N; public float W; }
            public class P
            {
                static int calls;
                static int Next(int v) { calls++; return v; }
                public static void Main()
                {
                    var l = new List<S> { new S { N = 1, W = 0.5f }, new S { N = 3, W = 2.5f }, new S { N = 2, W = -1f } };
                    l.Sort((x, y) => y.N.CompareTo(x.N));
                    Console.WriteLine(l[0].N + "," + l[1].N + "," + l[2].N);
                    l.Sort((x, y) => x.W.CompareTo(y.W));
                    Console.WriteLine(l[0].N + "," + l[1].N + "," + l[2].N);
                    var li = new List<int> { 1, 3, 2 };
                    li.Sort((x, y) => y.CompareTo(x));
                    Console.WriteLine(li[0] + "," + li[1] + "," + li[2]);
                    int a = 3;
                    Console.WriteLine(a.CompareTo(2) + ":" + a.CompareTo(3) + ":" + a.CompareTo(9) + ":" + int.MinValue.CompareTo(1) + ":" + int.MaxValue.CompareTo(-1));
                    float pz = 0f;
                    float nan = pz / pz;
                    float nz = -0f;
                    Console.WriteLine(1.5f.CompareTo(2.5f) + ":" + nan.CompareTo(1f) + ":" + 1f.CompareTo(nan) + ":" + nan.CompareTo(nan) + ":" + pz.CompareTo(nz) + ":" + 2f.CompareTo(1));
                    int? maybe = 4;
                    int? none = null;
                    Console.WriteLine((maybe?.CompareTo(5) ?? 9) + ":" + (none?.CompareTo(5) ?? 9));
                    Console.WriteLine(Next(1).CompareTo(Next(2)) + ":" + calls);
                }
            }
            """, "P");
        Assert.Equal("""
            3,2,1
            2,1,3
            3,2,1
            1:0:-1:-1:1
            -1:-1:1:0:0:1
            -1:9
            -1:2
            """.ReplaceLineEndings("\n"), output);
    }
}
