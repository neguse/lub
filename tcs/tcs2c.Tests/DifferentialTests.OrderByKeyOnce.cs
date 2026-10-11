namespace TinyCs.Tcs2c.Tests;

// OrderBy / OrderByDescending は key の selector を要素ごとに 1 回、要素の順に
// 呼ぶ (CoreCLR と同じ)。selector の副作用 (呼び出し回数・記録・呼ぶたびに
// 変わる key) で見る。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void OrderBy_CallsKeySelectorOncePerElement()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Item { public string Name; public int Rank; public Item(string n, int r) { Name = n; Rank = r; } }
            public struct Pt { public int X; public string Tag; }
            public class P
            {
                static int calls;
                static string log = "";
                static int Mod3(int x) { calls++; log += x + ";"; return x % 3; }
                static int Tick(int x) { calls++; return calls; }
                static string Show(List<int> l) => string.Join(",", l.Select(n => n.ToString()));
                public static void Main()
                {
                    var nums = new List<int> { 5, 3, 9, 1, 7, 2, 8, 0, 6, 4, 11, 10 };
                    calls = 0; log = "";
                    var a = nums.OrderBy(Mod3).ToList();
                    Console.WriteLine(calls + " " + Show(a));
                    Console.WriteLine(log);
                    calls = 0; log = "";
                    var b = nums.OrderByDescending(Mod3).ToList();
                    Console.WriteLine(calls + " " + Show(b));
                    Console.WriteLine(log);
                    calls = 0;
                    Console.WriteLine(Show(nums.OrderBy(Tick).ToList()) + " " + calls);
                    calls = 0;
                    Console.WriteLine(Show(nums.OrderByDescending(Tick).ToList()) + " " + calls);
                    int local = 0;
                    var c = nums.OrderBy(x => { local++; return -x; }).ToList();
                    Console.WriteLine(local + " " + Show(c));
                    calls = 0;
                    var first = nums.OrderBy(Mod3).First();
                    Console.WriteLine(first + " " + calls);
                    int[] arr = new int[] { 4, 2, 6, 1 };
                    calls = 0;
                    Console.WriteLine(Show(arr.OrderBy(Tick).ToList()) + " " + calls);
                    var empty = new List<int>();
                    calls = 0;
                    Console.WriteLine(empty.OrderBy(Mod3).ToList().Count + " " + calls);
                    var pair = new List<int> { 2, 1 };
                    calls = 0;
                    Console.WriteLine(Show(pair.OrderBy(Mod3).ToList()) + " " + calls);
                    int sc = 0;
                    var items = new List<Item> { new Item("b", 2), new Item("a", 1), new Item("c", 2), new Item("d", 0) };
                    var byName = items.OrderByDescending(i => { sc++; return i.Name + i.Rank; }).ToList();
                    Console.WriteLine(sc + " " + string.Join(",", byName.Select(i => i.Name)));
                    int fc = 0;
                    float zero = 0.0f;
                    var floats = new List<float> { 2.5f, zero / zero, -1f, 2.5f, 0f };
                    var fs = floats.OrderBy(f => { fc++; return f; }).ToList();
                    Console.WriteLine(fc + " " + string.Join(",", fs.Select(f => f != f ? "N" : ((int)(f * 10)).ToString())));
                    int pc = 0;
                    var pts = new List<Pt> { new Pt { X = 3, Tag = "p" }, new Pt { X = 1, Tag = "q" }, new Pt { X = 3, Tag = "r" } };
                    var ps = pts.OrderBy(p => { pc++; return p.X; }).ToList();
                    Console.WriteLine(pc + " " + string.Join(",", ps.Select(p => p.Tag)));
                }
            }
            """, "P");
        Assert.Equal(
            "12 3,9,0,6,1,7,4,10,5,2,8,11\n" +
            "5;3;9;1;7;2;8;0;6;4;11;10;\n" +
            "12 5,2,8,11,1,7,4,10,3,9,0,6\n" +
            "5;3;9;1;7;2;8;0;6;4;11;10;\n" +
            "5,3,9,1,7,2,8,0,6,4,11,10 12\n" +
            "10,11,4,6,0,8,2,7,1,9,3,5 12\n" +
            "12 11,10,9,8,7,6,5,4,3,2,1,0\n" +
            "3 12\n" +
            "4,2,6,1 4\n" +
            "0 0\n" +
            "1,2 2\n" +
            "4 d,c,b,a\n" +
            "5 N,-10,0,25,25\n" +
            "3 q,p,r",
            output);
    }

    [CFact]
    public void OrderBy_StringKeysSurviveFrames()
    {
        // 文字列の key を毎フレーム新しく作り、並べた結果を static に残して
        // 次のフレームで読む (フレーム境界の GC の後も並べた List が残る)
        const string source = """
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class Program
            {
                static List<int> nums = new List<int>();
                static List<int> sorted = new List<int>();
                static int calls;
                static int tick;
                static string Key(int x) { calls++; return "k" + ((x * 7 + tick) % 5) + "-" + x; }
                public static void Setup()
                {
                    for (int i = 0; i < 30; i++) nums.Add(i);
                }
                public static void Frame()
                {
                    tick++;
                    calls = 0;
                    var before = sorted.Count > 0 ? sorted[0] + "," + sorted[sorted.Count - 1] : "-";
                    sorted = nums.OrderByDescending(Key).ToList();
                    Console.WriteLine(tick + " " + calls + " " + before + " " + sorted[0] + "," + sorted[29]);
                }
                public static void Report()
                {
                    Console.WriteLine(string.Join(",", sorted.Select(n => n.ToString())));
                }
            }
            """;
        var output = Backends.AssertParityLib(source, "Program", 4);
        Assert.Equal(
            "1 30 - 9,12\n2 30 9,12 6,14\n3 30 6,14 8,1\n4 30 8,1 5,13\n" +
            "5,25,20,15,10,0,7,27,22,2,17,12,9,4,29,24,19,14,6,26,21,16,11,1,8,3,28,23,18,13",
            output);
    }
}
