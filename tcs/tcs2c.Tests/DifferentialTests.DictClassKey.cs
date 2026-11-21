namespace TinyCs.Tcs2c.Tests;

// user の class を key にした Dictionary は参照で比べる (.NET の EqualityComparer<K>.Default と同じ)。
// 同じ値の field を持つ別の instance は別の key。
// 列挙は挿入順 (support-matrix §18)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Dict_ClassKey_ComparesByReference()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class K { public int N; public K(int n) { N = n; } }
            public class Holder { public Dictionary<K, string> Names = new Dictionary<K, string>(); }
            public static class P
            {
                static Dictionary<K, int> Shared = new Dictionary<K, int>();
                static string Line(Dictionary<K, int> d)
                {
                    var r = d.Count + ":";
                    foreach (var kv in d) r += kv.Key.N + "=" + kv.Value + ",";
                    return r;
                }
                public static void Main()
                {
                    var a = new K(1);
                    var b = new K(1);
                    var d = new Dictionary<K, int>();
                    d[a] = 1;
                    Console.WriteLine(d.ContainsKey(a));
                    Console.WriteLine(d.ContainsKey(b));
                    d[b] = 2;
                    d[a] = 10;
                    Console.WriteLine(Line(d));
                    d.Add(new K(3), 3);
                    Console.WriteLine(d[a] + "," + d[b] + "," + d.Count);
                    Console.WriteLine(d.TryGetValue(b, out var got) + "," + got);
                    Console.WriteLine(d.TryGetValue(new K(1), out var missing) + "," + missing);
                    Console.WriteLine(d.Remove(a) + "," + d.Remove(a) + "," + d.ContainsKey(a));
                    d[a] = 4;
                    Console.WriteLine(Line(d));
                    var keys = "";
                    foreach (var k in d.Keys) keys += k.N + ",";
                    Console.WriteLine(keys);
                    var values = "";
                    foreach (var v in d.Values) values += v + ",";
                    Console.WriteLine(values);
                    Console.WriteLine(d.Keys.Count(k => k.N == 1) + "," + d.Keys.Where(k => k == a).Count);

                    // 初期化子、field と static の Dictionary
                    var init = new Dictionary<K, int> { [a] = 5, [b] = 6 };
                    var pairs = new Dictionary<K, int> { { b, 7 }, { a, 8 } };
                    Console.WriteLine(Line(init) + " " + Line(pairs));
                    var holder = new Holder();
                    holder.Names[a] = "a";
                    holder.Names[b] = "b";
                    Console.WriteLine(holder.Names[a] + holder.Names[b]);
                    Shared[b] = 9;
                    Console.WriteLine(Shared.ContainsKey(b) + "," + Shared.ContainsKey(a));

                    // 伸ばす経路、消した key の再追加、Clear
                    var many = new List<K>();
                    var big = new Dictionary<K, int>();
                    for (var i = 0; i < 200; i++)
                    {
                        var k = new K(i);
                        many.Add(k);
                        big[k] = i * 3;
                    }
                    for (var i = 0; i < 200; i += 4) big.Remove(many[i]);
                    var hits = 0;
                    var sum = 0;
                    foreach (var k in many) if (big.TryGetValue(k, out var v)) { hits++; sum += v; }
                    Console.WriteLine(big.Count + "," + hits + "," + sum);
                    big[many[0]] = -1;
                    var last = 0;
                    foreach (var kv in big) last = kv.Key.N;
                    Console.WriteLine(last + "," + big[many[0]]);
                    big.Clear();
                    Console.WriteLine(big.Count + "," + big.ContainsKey(many[1]));
                }
            }
            """, "P");
        Assert.Equal("""
            True
            False
            2:1=10,1=2,
            10,2,3
            True,2
            False,0
            True,False,False
            3:1=2,3=3,1=4,
            1,3,1,
            2,3,4,
            2,1
            2:1=5,1=6, 2:1=7,1=8,
            ab
            True,False
            150,150,45000
            0,-1
            0,False
            """.Replace("\r\n", "\n"), output);
    }

    // null の key は .NET が ArgumentNullException を投げ、tcs は fault で止まる (il-spec §12)
    [CFact]
    public void Dict_ClassKey_NullKeyFaults()
    {
        var output = Backends.AssertFaultParity("""
            using System;
            using System.Collections.Generic;
            public class K { public int N; }
            public static class P
            {
                public static void Main()
                {
                    var d = new Dictionary<K, int>();
                    d[new K()] = 1;
                    Console.WriteLine("before");
                    K none = null;
                    Console.WriteLine(d.ContainsKey(none));
                    Console.WriteLine("after");
                }
            }
            """, "P");
        Assert.Equal("before", output);
    }

    // C backend の class の key の hash は address で、GC の境界の昇格で address が変わっても引ける。
    // 若い key を入れた後に同じフレームの中で広げても、境界を跨いで引ける。
    // 列挙は挿入順
    [CFact]
    public void Dict_ClassKey_SurvivesFrameBoundaries()
    {
        var output = Backends.AssertParityLib("""
            using System;
            using System.Collections.Generic;
            public class Node { public string Name; public int Id; public Node(string name, int id) { Name = name; Id = id; } }
            public static class L
            {
                static Dictionary<Node, int> Index;
                static List<Node> Keep;
                static List<Node> Olds;
                static Dictionary<Node, int> Fresh;
                static List<Node> FreshKeys;
                static int Frames;
                static int Hits;
                static int FreshHits;
                public static void Setup()
                {
                    Index = new Dictionary<Node, int>();
                    Keep = new List<Node>();
                    Olds = new List<Node>();
                    for (var i = 0; i < 40; i++) Olds.Add(new Node("o" + i, i));
                }
                public static void Frame()
                {
                    Frames++;
                    var n = new Node("n" + Frames, Frames);
                    Keep.Add(n);
                    Index[n] = Frames;
                    foreach (var k in Keep) if (Index.TryGetValue(k, out var v) && v == k.Id) Hits++;
                    if (Frames % 3 == 0)
                    {
                        Index.Remove(Keep[0]);
                        Keep.RemoveAt(0);
                    }
                    // 前のフレームで作った Dictionary を引く: 若い key を入れた後に古い key だけで伸ばしたもの
                    if (Fresh != null) foreach (var k in FreshKeys) if (Fresh.ContainsKey(k)) FreshHits++;
                    Fresh = new Dictionary<Node, int>();
                    FreshKeys = new List<Node>();
                    for (var i = 0; i < 3; i++)
                    {
                        var young = new Node("y" + i, i);
                        FreshKeys.Add(young);
                        Fresh[young] = i;
                    }
                    foreach (var old in Olds)
                    {
                        FreshKeys.Add(old);
                        Fresh[old] = 0;
                    }
                }
                public static void Report()
                {
                    Console.WriteLine(Index.Count + "," + Hits + "," + FreshHits);
                    var line = "";
                    foreach (var kv in Index) line += kv.Key.Name + "=" + kv.Value + ",";
                    Console.WriteLine(line);
                }
            }
            """, "L", 30);
        Assert.Equal("20,330,1247\nn11=11,n12=12,n13=13,n14=14,n15=15,n16=16,n17=17,n18=18,n19=19,n20=20,n21=21,n22=22,n23=23,n24=24,n25=25,n26=26,n27=27,n28=28,n29=29,n30=30,", output);
    }

    // HashSet の class の要素も、若い要素を入れた後に古い要素だけで伸ばしたフレームの次のフレームで引ける
    [CFact]
    public void HashSet_ClassElement_GrownWithOldElements_SurvivesFrameBoundary()
    {
        var output = Backends.AssertParityLib("""
            using System;
            using System.Collections.Generic;
            public class Node { public int Id; public Node(int id) { Id = id; } }
            public static class L
            {
                static List<Node> Olds;
                static HashSet<Node> Set;
                static List<Node> Members;
                static int Hits;
                public static void Setup()
                {
                    Olds = new List<Node>();
                    for (var i = 0; i < 8; i++) Olds.Add(new Node(100 + i));
                }
                public static void Frame()
                {
                    if (Set != null) foreach (var m in Members) if (Set.Contains(m)) Hits++;
                    Set = new HashSet<Node>();
                    Members = new List<Node>();
                    for (var i = 0; i < 3; i++)
                    {
                        var young = new Node(i);
                        Members.Add(young);
                        Set.Add(young);
                    }
                    foreach (var old in Olds)
                    {
                        Members.Add(old);
                        Set.Add(old);
                    }
                }
                public static void Report()
                {
                    Console.WriteLine(Set.Count + "," + Hits);
                }
            }
            """, "L", 20);
        Assert.Equal("11,209", output);
    }
}
