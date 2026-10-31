namespace TinyCs.Tcs2c.Tests;

// HashSet<T>。列挙の順も .NET と同じ。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void HashSet_MatchesCoreClr()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Node { public int V; public Node(int v) { V = v; } }
            public enum Color { Red, Green, Blue }
            public class Holder { public HashSet<int> S = new HashSet<int> { 1 }; }
            public static class P
            {
                static HashSet<string> Field = new HashSet<string> { "f", "f", "g" };
                static string J(HashSet<int> s) { var r = s.Count + ":"; foreach (var v in s) r += v + ","; return r; }
                static string T(HashSet<string> s) { var r = s.Count + ":"; foreach (var v in s) r += (v ?? "null") + ","; return r; }
                static int Order;
                static int Next(int v) { Order = Order * 10 + v; return v; }
                public static void Main()
                {
                    var s = new HashSet<int>();
                    Console.WriteLine(J(s));
                    Console.WriteLine(s.Add(5));
                    Console.WriteLine(s.Add(5));
                    s.Add(3);
                    s.Add(9);
                    s.Add(-1);
                    Console.WriteLine(J(s));
                    Console.WriteLine(s.Contains(3) + "," + s.Contains(4));
                    Console.WriteLine(s.Remove(3));
                    Console.WriteLine(s.Remove(3));
                    Console.WriteLine(J(s));
                    s.Remove(9);
                    s.Add(100);
                    s.Add(200);
                    s.Add(300);
                    Console.WriteLine(J(s));
                    s.Clear();
                    Console.WriteLine(J(s));
                    s.Add(1);
                    s.Add(2);
                    Console.WriteLine(J(s));

                    // 伸ばす経路と free の再利用を混ぜても順が同じ
                    var big = new HashSet<int>();
                    for (var i = 0; i < 500; i++) big.Add(i * 7);
                    for (var i = 0; i < 500; i += 3) big.Remove(i * 7);
                    for (var i = 0; i < 50; i++) big.Add(10000 + i);
                    var h = 0;
                    foreach (var v in big) h = (h * 31 + v) % 1000003;
                    Console.WriteLine(big.Count + "," + h);

                    var t = new HashSet<string> { "a", null, "b", "a", null };
                    Console.WriteLine(T(t));
                    Console.WriteLine(t.Contains(null) + "," + t.Contains("a") + "," + t.Contains("z"));
                    Console.WriteLine(t.Remove(null));
                    Console.WriteLine(t.Contains(null));
                    Console.WriteLine(T(t));
                    t.Add(null);
                    Console.WriteLine(T(t));
                    var key = "x";
                    t.Add(key + "y");
                    Console.WriteLine(t.Contains("xy"));

                    var nodes = new HashSet<Node>();
                    var n1 = new Node(1);
                    var n2 = new Node(1);
                    Console.WriteLine(nodes.Add(n1) + "," + nodes.Add(n1) + "," + nodes.Add(n2));
                    Console.WriteLine(nodes.Contains(new Node(1)) + "," + nodes.Contains(n2));
                    nodes.Add(null);
                    Console.WriteLine(nodes.Count + "," + nodes.Remove(n1) + "," + nodes.Count);
                    foreach (var n in nodes) Console.WriteLine(n == null ? -1 : n.V);

                    var colors = new HashSet<Color> { Color.Blue, Color.Red };
                    Console.WriteLine(colors.Contains(Color.Blue) + "," + colors.Contains(Color.Green));
                    var chars = new HashSet<char> { 'a' };
                    Console.WriteLine(chars.Contains('a'));

                    var init = new HashSet<int> { Next(1), Next(2), Next(3) };
                    Console.WriteLine(Order + "," + init.Count);
                    Console.WriteLine(T(Field));
                    var holder = new Holder();
                    Console.WriteLine(holder.S.Count + (holder.S?.Count ?? 0));
                    Console.WriteLine(holder.S?.Add(2));
                    holder.S?.Clear();
                    Console.WriteLine(holder.S.Count);
                }
            }
            """, "P");
        Assert.Equal("""
            0:
            True
            False
            4:5,3,9,-1,
            True,False
            True
            False
            3:5,9,-1,
            5:5,200,100,-1,300,
            0:
            2:1,2,
            383,646976
            3:a,null,b,
            True,True,False
            True
            False
            2:a,b,
            3:a,null,b,
            True
            True,False,True
            False,True
            3,True,2
            1
            -1
            True,False
            True
            123,3
            2:f,g,
            2
            True
            0
            """.Replace("\r\n", "\n"), output);
    }

    // class の要素の hash は address。GC の境界の昇格で address が変わっても引ける
    [CFact]
    public void HashSet_SurvivesFrameBoundaries()
    {
        var output = Backends.AssertParityLib("""
            using System;
            using System.Collections.Generic;
            public class Node { public string Name; public Node(string name) { Name = name; } }
            public static class L
            {
                static HashSet<Node> Nodes;
                static HashSet<string> Names;
                static List<Node> Keep;
                static int Frames;
                static int Hits;
                public static void Setup()
                {
                    Nodes = new HashSet<Node>();
                    Names = new HashSet<string>();
                    Keep = new List<Node>();
                }
                public static void Frame()
                {
                    Frames++;
                    var n = new Node("n" + Frames);
                    Keep.Add(n);
                    Nodes.Add(n);
                    Names.Add(n.Name);
                    foreach (var k in Keep) if (Nodes.Contains(k) && Names.Contains(k.Name)) Hits++;
                    if (Frames % 3 == 0)
                    {
                        Nodes.Remove(Keep[0]);
                        Names.Remove(Keep[0].Name);
                        Keep.RemoveAt(0);
                    }
                }
                public static void Report()
                {
                    Console.WriteLine(Nodes.Count + "," + Names.Count + "," + Hits);
                    var line = "";
                    foreach (var n in Nodes) line += n.Name + ",";
                    Console.WriteLine(line);
                }
            }
            """, "L", 30);
        Assert.Equal("20,20,330\nn13,n22,n16,n19,n25,n28,n11,n12,n14,n15,n17,n18,n20,n21,n23,n24,n26,n27,n29,n30,", output);
    }

    // 値で比べる要素と float は TCS1001 で両 backend とも止まる
    [Theory]
    [InlineData("new HashSet<S>()", "UnsupportedHashSetElement(S)")]
    [InlineData("new HashSet<R>()", "UnsupportedHashSetElement(R)")]
    [InlineData("new HashSet<float>()", "UnsupportedHashSetElement(float)")]
    public void HashSet_UnsupportedElements(string creation, string syntax)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            public struct S { public int X; }
            public record R(int X);
            public static class P
            {
                public static void Main()
                {
                    var s = {{creation}};
                    Console.WriteLine(s.Count);
                }
            }
            """;
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"TCS1001: unsupported syntax: {syntax}", ex.Message);
        }
    }

    [Theory]
    [InlineData("var s = new HashSet<int>(new List<int>());", "System.Collections.Generic.HashSet<T>")]
    [InlineData("var s = new HashSet<int>(); s.UnionWith(new List<int>());", "System.Collections.Generic.HashSet<T>.UnionWith")]
    public void HashSet_UnsupportedMembers(string statement, string api)
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
