namespace TinyCs.Tcs2c.Tests;

// Dictionary の値が struct のときの member 読み (foreach の kv.Value.F、d[k].F、
// 入れ子の struct field)。kv.Value は値の copy なので、その上の mutating method は
// Dictionary を変えない。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void DictStructValue_MemberReads()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Inner { public int N; public string T; }
            public struct Rec
            {
                public string S; public int N; public Inner In;
                public void Bump() { N += 100; S = S + "!"; }
                public int Twice() { return N * 2; }
            }
            public class P
            {
                public static void Main()
                {
                    var d = new Dictionary<string, Rec>
                    {
                        ["a"] = new Rec { S = "x", N = 1, In = new Inner { N = 10, T = "p" } },
                    };
                    foreach (var kv in d)
                    {
                        Console.WriteLine(kv.Value.N);
                        Console.WriteLine(kv.Value.S + kv.Key);
                        Console.WriteLine(kv.Value.In.N + ":" + kv.Value.In.T);
                        Console.WriteLine(kv.Value.Twice());
                        kv.Value.Bump();
                        Console.WriteLine(kv.Value.N + kv.Value.S);
                        var copy = kv.Value;
                        copy.N = 5;
                        copy.In.N = 6;
                        Console.WriteLine(kv.Value.N + "," + kv.Value.In.N + "," + copy.N + "," + copy.In.N);
                    }
                    Console.WriteLine(d["a"].N + d["a"].S);
                    Console.WriteLine(d["a"].In.N + d["a"].In.T);
                    var byId = new Dictionary<int, Rec>();
                    for (int i = 0; i < 3; i++)
                        byId[i] = new Rec { S = "s" + i, N = i, In = new Inner { N = i * 10, T = "t" + i } };
                    int sum = 0;
                    string text = "";
                    foreach (var kv in byId)
                    {
                        sum += kv.Value.N + kv.Value.In.N + kv.Key;
                        if (kv.Key == 2) text = kv.Value.S + kv.Value.In.T;
                    }
                    Console.WriteLine(sum + text);
                }
            }
            """, "P");
        Assert.Equal("1\nxa\n10:p\n2\n1x\n1,10,5,6\n1x\n10p\n36s2t2", output);
    }

    [CFact]
    public void DictStructValue_CapturedPairAndGc()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            using System.Collections.Generic;
            public struct Inner { public string T; }
            public struct Rec { public string S; public Inner In; }
            public class P
            {
                public static void Main()
                {
                    var d = new Dictionary<int, Rec>();
                    for (int i = 0; i < 40; i++)
                        d[i] = new Rec { S = "s" + i, In = new Inner { T = "t" + i } };
                    var readers = new List<Func<string>>();
                    foreach (var kv in d)
                        if (kv.Key == 7 || kv.Key == 31)
                            readers.Add(() => kv.Value.S + kv.Value.In.T);
                    d[7] = new Rec { S = "changed", In = new Inner { T = "!" } };
                    d.Remove(31);
                    string garbage = "";
                    for (int i = 0; i < 200; i++) garbage = "g" + i + garbage.RuneCount();
                    int total = 0;
                    foreach (var kv in d) total += kv.Value.S.RuneCount() + kv.Value.In.T.RuneCount();
                    var parts = new List<string>();
                    foreach (var r in readers) parts.Add(r());
                    parts.Sort();
                    Console.WriteLine(string.Join(",", parts));
                    Console.WriteLine(total + "," + d[7].S + d[7].In.T + "," + garbage.RuneCount());
                }
            }
            """, "P");
        Assert.Equal("s31t31,s7t7\n218,changed!,5", output);
    }
}
