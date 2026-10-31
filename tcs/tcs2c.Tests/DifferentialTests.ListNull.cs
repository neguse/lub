namespace TinyCs.Tcs2c.Tests;

// List に null を入れても Count と順序は C# と同じ。Lua は長さを field `n` に持ち、添字の範囲を
// 長さと比べる。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private const string ListNullProgram = """
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class Node { public int V; public Node(int v) { V = v; } }
            public static class P
            {
                static string S(List<string> l) { var r = l.Count + ":"; foreach (var s in l) r += (s ?? "null") + ","; return r; }
                static string N(List<Node> l) { var r = l.Count + ":"; foreach (var n in l) r += (n == null ? "null" : "" + n.V) + ","; return r; }
                static int CountOf<T>(List<T> l) { var n = 0; foreach (var _ in l) n++; return n * 100 + l.Count; }
                static void Push<T>(List<T> l, T v) { l.Add(v); }
                static List<string> Field = new List<string> { "x", null };
                public static void Main()
                {
                    var a = new List<string>();
                    a.Add(null);
                    Console.WriteLine(S(a));
                    a.Add("b");
                    a.Add(null);
                    a.Add(null);
                    Console.WriteLine(S(a));
                    Console.WriteLine(a[0] == null);
                    Console.WriteLine(a[1]);
                    Console.WriteLine(a[3] == null);
                    a[1] = null;
                    a[0] = "z";
                    Console.WriteLine(S(a));
                    Console.WriteLine(a.IndexOf(null));
                    Console.WriteLine(a.Contains(null));
                    Console.WriteLine(a.Remove(null));
                    Console.WriteLine(S(a));
                    a.RemoveAt(1);
                    Console.WriteLine(S(a));
                    a.RemoveAt(a.Count - 1);
                    Console.WriteLine(S(a));
                    a.Add("q");
                    Console.WriteLine(S(a));
                    Console.WriteLine(a.FindIndex(s => s == "q"));
                    Console.WriteLine(a.RemoveAll(s => s == null));
                    Console.WriteLine(S(a));
                    a.Clear();
                    Console.WriteLine(S(a));
                    a.Add(null);
                    Console.WriteLine(S(a));

                    var lit = new List<string> { "a", null, "c", null };
                    Console.WriteLine(S(lit));
                    var copy = new List<string>(lit);
                    copy.Add(null);
                    Console.WriteLine(S(lit) + " " + S(copy));
                    Console.WriteLine(string.Join("|", lit));
                    lit.Sort();
                    Console.WriteLine(S(lit));
                    lit.Sort((x, y) => (y == null ? 0 : 1) - (x == null ? 0 : 1));
                    Console.WriteLine(lit[0] == null ? "null first" : "value first");
                    Console.WriteLine(Field.Count);
                    Field.Add(null);
                    Console.WriteLine(Field.Count + S(Field));

                    var nodes = new List<Node> { new Node(1), null };
                    nodes.Add(default);
                    nodes[0] = null;
                    nodes.Add(new Node(2));
                    Console.WriteLine(N(nodes));
                    nodes[1] = new Node(9);
                    var total = 0;
                    foreach (var n in nodes) { if (n == null) continue; total += n.V; }
                    Console.WriteLine(total);
                    Console.WriteLine(nodes[2]?.V ?? -1);
                    Console.WriteLine(nodes.Where(n => n != null).Count);
                    Console.WriteLine(nodes.Select(n => n == null ? 0 : n.V).Sum());

                    var opt = new List<int?> { 1, null, 3 };
                    opt.Add(null);
                    Console.WriteLine(opt.Count);
                    Console.WriteLine(opt[1].HasValue);
                    Console.WriteLine(opt[2].Value);
                    Console.WriteLine(opt.IndexOf(null));
                    opt[0] = null;
                    var some = 0;
                    foreach (var o in opt) if (o.HasValue) some++;
                    Console.WriteLine(some);

                    var bools = new List<bool?> { true, false, null };
                    Console.WriteLine(bools[1] == false);
                    Console.WriteLine(bools[2] == null);
                    Console.WriteLine(bools.Count);

                    var objs = new List<object> { false, null };
                    Console.WriteLine(objs.Count);
                    Console.WriteLine(objs[1] == null);

                    Console.WriteLine(CountOf(new List<string> { null, null }));
                    Console.WriteLine(CountOf(new List<int> { 1, 2, 3 }));
                    var ints = new List<int> { 1, 2, 3 };
                    ints.RemoveAt(0);
                    ints.Add(4);
                    ints.Remove(3);
                    Console.WriteLine(ints.Count * 10 + ints[1]);
                    var pushed = new List<int>();
                    Push(pushed, 1);
                    pushed.Add(2);
                    Push(pushed, 3);
                    Console.WriteLine(pushed.Count * 10 + pushed[2]);
                    var pushedRef = new List<string>();
                    Push(pushedRef, "a");
                    Push(pushedRef, null);
                    pushedRef.Add("b");
                    Push(pushedRef, null);
                    Console.WriteLine(S(pushedRef));
                }
            }
            """;

    [CFact]
    public void ListNull_MatchesCoreClr()
    {
        Assert.Equal("1:null,\n4:null,b,null,null,\nTrue\nb\nTrue\n4:z,null,null,null,\n1\nTrue\nTrue\n3:z,null,null,\n2:z,null,\n1:z,\n2:z,q,\n1\n0\n2:z,q,\n0:\n1:null,\n4:a,null,c,null,\n4:a,null,c,null, 5:a,null,c,null,null,\na||c|\n4:null,null,a,c,\nvalue first\n2\n33:x,null,null,\n4:null,null,null,2,\n11\n-1\n2\n11\n4\nFalse\n3\n1\n1\nTrue\nTrue\n3\n2\nTrue\n202\n303\n24\n33\n4:a,null,b,null,",
            Backends.AssertParity(ListNullProgram, "P"));
    }

    // 長さの外は null の入った List でも fault。範囲内の null は読み書きできる
    [CTheory]
    [InlineData("var l = new List<string> { null }; Console.WriteLine(l[1] == null);")]
    [InlineData("var l = new List<string> { null }; Console.WriteLine(l[-1] == null);")]
    [InlineData("var l = new List<string> { null }; l[1] = \"b\";")]
    [InlineData("var l = new List<string> { null }; l[-1] = null;")]
    [InlineData("var l = new List<string> { null }; Get(l)[1] = \"b\";")]
    [InlineData("var l = new List<int?> { null }; Console.WriteLine(Get(l)[2].HasValue);")]
    [InlineData("var l = new List<string> { null }; l.RemoveAt(1);")]
    [InlineData("var l = new List<object> { false }; Console.WriteLine(l[1] == null);")]
    public void ListNull_OutOfRangeFaults(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                static List<T> Get<T>(List<T> l) => l;
                public static void Main()
                {
                    Console.WriteLine("before");
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // 受け手が式の Add / Count (変数を読み直せない形)、initializer の入れ子、compound
    [CFact]
    public void ListNull_ExpressionReceivers()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box { public List<string> Items = new List<string>(); }
            public static class P
            {
                static List<string> L = new List<string>();
                static List<string> Get() => L;
                static int Calls;
                static List<int> Nums() { Calls++; return new List<int> { 1, 2 }; }
                public static void Main()
                {
                    Get().Add(null);
                    Get().Add("x");
                    Console.WriteLine(Get().Count);
                    Console.WriteLine(Get()[0] == null);
                    Console.WriteLine(Get()?.Count);
                    Get()[0] = "y";
                    Console.WriteLine(Get()[0] + L[1]);
                    Console.WriteLine(Nums().Count + Calls);
                    var box = new Box { Items = { null, "a", null } };
                    Console.WriteLine(box.Items.Count);
                    box.Items[1] += "b";
                    Console.WriteLine(box.Items[1]);
                    Action<string> push = s => box.Items.Add(s);
                    push(null);
                    Console.WriteLine(box.Items.Count);
                }
            }
            """, "P");
        Assert.Equal("2\nTrue\n2\nyx\n3\n3\nab\n4", output);
    }

    // 長さを field に持たない host の table も List として読める
    [Fact]
    public void ListNull_HostTableWithoutCountReadsAsList()
    {
        var output = Backends.RunLua(["""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static List<int> FromHost;
                public static void Main()
                {
                    var l = FromHost;
                    Console.WriteLine(l.Count);
                    foreach (var v in l) Console.WriteLine(v);
                    l.Add(7);
                    l.RemoveAt(0);
                    Console.WriteLine(l.Count + "," + l[1]);
                }
            }
            """], "P", "P.FromHost = {10, 20}");
        Assert.Equal("2\n10\n20\n2,7", output.Replace("\r\n", "\n").TrimEnd('\n'));
    }
}
