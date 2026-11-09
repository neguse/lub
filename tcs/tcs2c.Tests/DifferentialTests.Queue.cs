namespace TinyCs.Tcs2c.Tests;

// Queue<T>。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Queue_MatchesCoreClr()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct P2 { public int X; public P2(int x) { X = x; } }
            public class Node { public int V; public Node(int v) { V = v; } }
            public class Holder { public Queue<int> Q = new Queue<int>(); }
            public static class P
            {
                static Queue<int> Field = new Queue<int>();
                public static void Main()
                {
                    var q = new Queue<int>();
                    Console.WriteLine(q.Count);
                    q.Enqueue(1);
                    q.Enqueue(2);
                    q.Enqueue(3);
                    Console.WriteLine(q.Count + "," + q.Peek());
                    Console.WriteLine(q.Dequeue());
                    Console.WriteLine(q.Contains(1) + "," + q.Contains(2));
                    q.Enqueue(4);
                    var sum = 0;
                    foreach (var v in q) sum = sum * 10 + v;
                    Console.WriteLine(sum);
                    while (q.Count > 0) sum += q.Dequeue();
                    Console.WriteLine(sum + "," + q.Count);
                    q.Enqueue(7);
                    q.Clear();
                    Console.WriteLine(q.Count);
                    q.Enqueue(8);
                    Console.WriteLine(q.Peek());

                    // 先頭の空きを詰める経路と伸ばす経路を両方通す
                    var r = new Queue<int>();
                    var total = 0;
                    for (var i = 0; i < 1000; i++)
                    {
                        r.Enqueue(i);
                        if (i % 3 != 0) total += r.Dequeue();
                    }
                    Console.WriteLine(total + "," + r.Count + "," + r.Peek());

                    var s = new Queue<string>();
                    s.Enqueue(null);
                    s.Enqueue("a");
                    s.Enqueue(null);
                    Console.WriteLine(s.Count);
                    Console.WriteLine(s.Contains(null));
                    Console.WriteLine(s.Peek() == null);
                    var line = "";
                    foreach (var x in s) line += (x ?? "null") + ",";
                    Console.WriteLine(line);
                    Console.WriteLine(s.Dequeue() == null);
                    Console.WriteLine(s.Dequeue());
                    Console.WriteLine(s.Dequeue() == null);
                    Console.WriteLine(s.Contains(null));

                    var structs = new Queue<P2>();
                    var p = new P2(1);
                    structs.Enqueue(p);
                    p.X = 2;
                    structs.Enqueue(p);
                    var peeked = structs.Peek();
                    peeked.X = 50;
                    Console.WriteLine(structs.Peek().X + "," + peeked.X);
                    Console.WriteLine(structs.Contains(new P2(2)) + "," + structs.Contains(new P2(3)));
                    foreach (var it in structs) { var c = it; c.X = 9; }
                    Console.WriteLine(structs.Dequeue().X + "," + structs.Dequeue().X);

                    var nodes = new Queue<Node>();
                    var n = new Node(5);
                    nodes.Enqueue(n);
                    Console.WriteLine(nodes.Contains(n) + "," + nodes.Contains(new Node(5)));
                    Console.WriteLine(nodes.Peek().V);

                    var fs = new Queue<Func<int, int>>();
                    fs.Enqueue(v => v + 1);
                    Console.WriteLine(fs.Dequeue()(1));

                    var nan = new Queue<float>();
                    nan.Enqueue(0f / 0f);
                    Console.WriteLine(nan.Contains(0f / 0f));

                    var h = new Holder();
                    h.Q.Enqueue(1);
                    Console.WriteLine(h.Q.Count + (h.Q?.Count ?? 0));
                    h.Q?.Enqueue(2);
                    Console.WriteLine(h.Q?.Peek());
                    Field.Enqueue(6);
                    Console.WriteLine(Field.Dequeue());
                }
            }
            """, "P");
        Assert.Equal("""
            0
            3,1
            1
            False,True
            234
            243,0
            0
            8
            221445,334,666
            3
            True
            True
            null,a,null,
            True
            a
            True
            False
            1,50
            True,False
            1,2
            True,False
            5
            2
            True
            2
            1
            6
            """.Replace("\r\n", "\n"), output);
    }

    // フレーム境界の GC を跨いでも要素が生きている (nursery から旧世代への昇格、stress の毒値)
    [CFact]
    public void Queue_SurvivesFrameBoundaries()
    {
        var output = Backends.AssertParityLib("""
            using System;
            using System.Collections.Generic;
            public class Node { public string Name; public Node(string name) { Name = name; } }
            public static class L
            {
                static Queue<Node> Q;
                static Queue<string> S;
                static int Frames;
                static int Seen;
                public static void Setup()
                {
                    Q = new Queue<Node>();
                    S = new Queue<string>();
                }
                public static void Frame()
                {
                    Frames++;
                    Q.Enqueue(new Node("n" + Frames));
                    S.Enqueue("s" + Frames);
                    if (Frames % 2 == 0)
                    {
                        Seen += Q.Dequeue().Name.StartsWith("n") ? 2 : 0;
                        Seen += S.Dequeue().StartsWith("s") ? 2 : 0;
                    }
                }
                public static void Report()
                {
                    Console.WriteLine(Q.Count + "," + S.Count + "," + Seen);
                    Console.WriteLine(Q.Peek().Name + "," + S.Peek());
                    foreach (var n in Q) Seen += n.Name.StartsWith("n") ? 2 : 0;
                    Console.WriteLine(Seen);
                }
            }
            """, "L", 40);
        Assert.Equal("20,20,80\nn21,s21\n120", output);
    }

    [CTheory]
    [InlineData("var q = new Queue<int>(); q.Dequeue();")]
    [InlineData("var q = new Queue<int>(); q.Peek();")]
    [InlineData("var q = new Queue<string>(); q.Enqueue(null); q.Dequeue(); q.Dequeue();")]
    [InlineData("var q = new Queue<int>(); q.Enqueue(1); q.Clear(); q.Peek();")]
    public void Queue_EmptyFaults(string body)
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

    // 対応していない member は TCS1002。object への型 test は Lua では止まる (List と同じ)
    [Theory]
    [InlineData("var q = new Queue<int>(4);", "System.Collections.Generic.Queue<T>")]
    [InlineData("var q = new Queue<int>(); q.TryDequeue(out var v);", "System.Collections.Generic.Queue<T>.TryDequeue")]
    [InlineData("var q = new Queue<int>(); var a = q.ToArray();", "System.Collections.Generic.Queue<T>.ToArray")]
    public void Queue_UnsupportedMembers(string statement, string api)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    {{statement}}
                }
            }
            """;
        var ex = Assert.Throws<InvalidOperationException>(() => Backends.EmitC([program], "P"));
        Assert.Contains($"error TCS1002: unsupported API: {api}", ex.Message);
    }
}
