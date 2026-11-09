namespace TinyCs.Tcs2c.Tests;

// List.Insert と GetRange。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void ListInsertGetRange_MatchesCoreClr()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct P2 { public int X; public P2(int x) { X = x; } }
            public class Node { public int V; }
            public static class P
            {
                static int Order;
                static int Next(int v) { Order = Order * 10 + v; return v; }
                static string J(List<int> l) => l.Count + ":" + string.Join(",", l);
                static string S(List<string> l) { var r = l.Count + ":"; foreach (var s in l) r += (s ?? "null") + ","; return r; }
                public static void Main()
                {
                    var l = new List<int>();
                    l.Insert(0, 2);
                    l.Insert(0, 1);
                    l.Insert(2, 4);
                    l.Insert(2, 3);
                    Console.WriteLine(J(l));
                    var r = l.GetRange(1, 2);
                    Console.WriteLine(J(r));
                    r.Add(9);
                    r[0] = 7;
                    Console.WriteLine(J(l) + " " + J(r));
                    Console.WriteLine(J(l.GetRange(4, 0)));
                    Console.WriteLine(J(l.GetRange(0, 4)));

                    var s = new List<string> { "a", null };
                    s.Insert(1, null);
                    s.Insert(0, "z");
                    Console.WriteLine(S(s));
                    var sr = s.GetRange(2, 2);
                    Console.WriteLine(S(sr));
                    sr.Add(null);
                    Console.WriteLine(S(sr));
                    Console.WriteLine(S(s.GetRange(0, 2)));
                    var e = new List<string>();
                    e.Insert(0, null);
                    Console.WriteLine(S(e));
                    e.Insert(1, "t");
                    Console.WriteLine(S(e));

                    var structs = new List<P2> { new P2(1), new P2(2) };
                    var sub = structs.GetRange(0, 1);
                    var first = sub[0];
                    first.X = 50;
                    sub[0] = first;
                    Console.WriteLine(structs[0].X + "," + sub[0].X);
                    var p = new P2(3);
                    structs.Insert(0, p);
                    p.X = 4;
                    Console.WriteLine(structs[0].X + "," + structs.Count);

                    var order = new List<int> { 0 };
                    order.Insert(Next(1), Next(2));
                    Console.WriteLine(Order);
                    Func<int> f = () => 1;
                    var fs = new List<Func<int>>();
                    fs.Insert(0, f);
                    Console.WriteLine(fs[0]());
                    Console.WriteLine(new List<int> { 1, 2, 3 }?.GetRange(1, 1)[0]);
                }
            }
            """, "P");
        Assert.Equal("""
            4:1,2,3,4
            2:2,3
            4:1,2,3,4 3:7,3,9
            0:
            4:1,2,3,4
            4:z,a,null,null,
            2:null,null,
            3:null,null,null,
            2:z,a,
            1:null,
            2:null,t,
            1,50
            3,3
            12
            1
            2
            """.Replace("\r\n", "\n"), output);
    }

    [CTheory]
    [InlineData("var l = new List<int> { 1 }; l.Insert(2, 0);")]
    [InlineData("var l = new List<int> { 1 }; l.Insert(-1, 0);")]
    [InlineData("var l = new List<string> { null }; l.Insert(2, null);")]
    [InlineData("var l = new List<int> { 1 }; l.GetRange(0, 2);")]
    [InlineData("var l = new List<int> { 1 }; l.GetRange(2, 0);")]
    [InlineData("var l = new List<int> { 1 }; l.GetRange(-1, 1);")]
    [InlineData("var l = new List<int> { 1 }; l.GetRange(0, -1);")]
    [InlineData("var l = new List<string> { null, null }; l.GetRange(1, 2);")]
    public void ListInsertGetRange_OutOfRangeFaults(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }
}
