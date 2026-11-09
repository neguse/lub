namespace TinyCs.Tcs2c.Tests;

// List.Sort は .NET と同じ introsort (dotnet/runtime の ArraySortHelper<T>) で、同値の要素の並びと比較の呼び出し列まで同じ。
// ties は Sort(comparison) の同値の key (m 種類) の並びと、比較の回数と呼び出し列の hash。
// killer は McIlroy の adversary (比較の結果を遅延して決め、分割を最悪にする) で、depthLimit に届いて heapsort に落ちる。
// zeros は比較なしの float の Sort で、NaN を先頭に寄せた後の ±0 の並び (N は NaN、- と + は ±0、a-d は負から正の値)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void SortTies_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Item
            {
                public int Id;
                public int Key;
            }
            public class P
            {
                static int calls;
                static int trace;
                static int[] val = new int[0];
                static int gas;
                static int solid;
                static int candidate;
                static void Count(Item a, Item b)
                {
                    calls++;
                    trace = (trace * 31 + a.Id * 1009 + b.Id) % 1000003;
                }
                static int ByKey(Item a, Item b)
                {
                    Count(a, b);
                    return a.Key - b.Key;
                }
                static int Adversary(Item a, Item b)
                {
                    Count(a, b);
                    int x = a.Id;
                    int y = b.Id;
                    if (val[x] == gas && val[y] == gas)
                    {
                        if (x == candidate) val[x] = solid++;
                        else val[y] = solid++;
                    }
                    if (val[x] == gas) candidate = x;
                    else if (val[y] == gas) candidate = y;
                    return val[x] - val[y];
                }
                static string Order(List<Item> items)
                {
                    if (items.Count <= 17)
                    {
                        string ids = "";
                        for (int i = 0; i < items.Count; i++) ids += items[i].Id + ",";
                        return ids;
                    }
                    int h = 0;
                    for (int i = 0; i < items.Count; i++) h = (h * 31 + items[i].Id) % 1000003;
                    return "#" + h;
                }
                static List<Item> Make(int n, int m)
                {
                    var items = new List<Item>();
                    int seed = 12345;
                    for (int i = 0; i < n; i++)
                    {
                        seed = (seed * 75 + 74) % 65537;
                        items.Add(new Item { Id = i, Key = seed % m });
                    }
                    return items;
                }
                static void Ties(int n, int m)
                {
                    var items = Make(n, m);
                    calls = 0;
                    trace = 0;
                    items.Sort((a, b) => ByKey(a, b));
                    Console.WriteLine("ties " + n + " " + m + " " + calls + " " + trace + " " + Order(items));
                }
                static void Killer(int n)
                {
                    var items = Make(n, 1);
                    val = new int[n];
                    gas = n;
                    solid = 0;
                    candidate = 0;
                    for (int i = 0; i < n; i++) val[i] = gas;
                    calls = 0;
                    trace = 0;
                    items.Sort((a, b) => Adversary(a, b));
                    Console.WriteLine("killer " + n + " " + calls + " " + trace + " " + Order(items));
                }
                static string Floats(List<float> values)
                {
                    string result = "";
                    for (int i = 0; i < values.Count; i++)
                    {
                        float v = values[i];
                        result += v != v ? "N" : v == 0.0f ? (1.0f / v < 0.0f ? "-" : "+") : v < -1.0f ? "a" : v < 0.0f ? "b" : v < 1.0f ? "c" : "d";
                    }
                    return result;
                }
                static void Zeros(int n)
                {
                    float zero = 0.0f;
                    float nan = zero / zero;
                    var values = new List<float>();
                    for (int i = 0; i < n; i++)
                    {
                        int k = (i * 7 + 3) % 11;
                        values.Add(k < 3 ? -zero : k < 6 ? zero : k == 6 ? nan : (float)(k % 4) - 1.5f);
                    }
                    values.Sort();
                    Console.WriteLine("zeros " + n + " " + Floats(values));
                }
                public static void Main()
                {
                    var sizes = new List<int> { 2, 3, 16, 17, 100, 1000 };
                    var keys = new List<int> { 1, 2, 3, 7, 1000000 };
                    for (int i = 0; i < sizes.Count; i++)
                        for (int j = 0; j < keys.Count; j++)
                            Ties(sizes[i], keys[j]);
                    Killer(100);
                    Killer(1000);
                    var zeroSizes = new List<int> { 2, 3, 16, 17, 40, 200 };
                    for (int i = 0; i < zeroSizes.Count; i++) Zeros(zeroSizes[i]);
                }
            }
            """, "P");
        Assert.Equal("""
            ties 2 1 1 1 0,1,
            ties 2 2 1 1 1,0,
            ties 2 3 1 1 0,1,
            ties 2 7 1 1 0,1,
            ties 2 1000000 1 1 0,1,
            ties 3 1 3 2034 0,1,2,
            ties 3 2 3 32304 1,2,0,
            ties 3 3 3 2034 0,1,2,
            ties 3 7 3 2032 2,0,1,
            ties 3 1000000 3 2034 0,1,2,
            ties 16 1 15 690264 0,1,2,3,4,5,6,7,8,9,10,11,12,13,14,15,
            ties 16 2 38 972192 1,2,4,5,13,14,0,3,6,7,8,9,10,11,12,15,
            ties 16 3 63 555076 4,6,13,14,0,3,5,7,8,10,11,15,1,2,9,12,
            ties 16 7 65 651075 11,4,14,2,3,6,0,10,12,5,7,15,8,13,1,9,
            ties 16 1000000 74 552942 0,15,8,13,7,4,3,1,12,6,9,14,2,11,5,10,
            ties 17 1 33 318121 0,14,13,12,11,10,9,15,8,6,5,4,3,2,1,7,16,
            ties 17 2 33 619974 16,1,2,14,4,5,13,12,11,10,8,15,7,6,3,9,0,
            ties 17 3 44 658863 14,13,4,6,0,11,10,15,8,5,3,7,9,12,2,1,16,
            ties 17 7 44 301557 16,11,14,4,2,3,6,12,10,0,15,7,5,13,8,1,9,
            ties 17 1000000 90 523559 16,0,15,8,13,7,4,3,1,12,6,9,14,2,11,5,10,
            ties 100 1 390 334295 #424309
            ties 100 2 432 599904 #962573
            ties 100 3 439 922263 #133589
            ties 100 7 507 712743 #349315
            ties 100 1000000 761 418051 #284086
            ties 1000 1 6900 317491 #742673
            ties 1000 2 7457 867000 #444651
            ties 1000 3 7522 173465 #158631
            ties 1000 7 7665 889161 #263851
            ties 1000 1000000 10623 887572 #56397
            killer 100 1858 622136 #104632
            killer 1000 34864 264386 #951051
            zeros 2 +c
            zeros 3 N+c
            zeros 16 NNabb+-+-+-+-ccd
            zeros 17 NNabb+-+-+-+-+ccd
            zeros 40 NNNNaaabbbb-++-+---++--+-+-++-+-+ccccddd
            zeros 200 NNNNNNNNNNNNNNNNNNaaaaaaaaaaaaaaaaaabbbbbbbbbbbbbbbbbb--+-+--++-++-++-+--+-++-+--++-+--++--++++--+++-+--+------+--++---++-++--+-+++--+++-+++-+--++-+--+--++--++--++cccccccccccccccccccdddddddddddddddddd
            """.ReplaceLineEndings("\n"), output);
    }

    // 比較が不整合だと分割の走査が区間の外に出る。
    // .NET はそこで例外を投げるので、同じ比較の後で fault する
    [CFact]
    public void SortInconsistentComparison_FaultsLikeDotnet()
    {
        var output = Backends.AssertFaultParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                public static void Main()
                {
                    var values = new List<int>();
                    for (int i = 0; i < 20; i++) values.Add(i);
                    values.Sort((a, b) =>
                    {
                        Console.WriteLine(a + ":" + b);
                        return -1;
                    });
                    Console.WriteLine("unreachable");
                }
            }
            """, "P");
        Assert.Equal("""
            0:9
            0:19
            9:19
            1:9
            2:9
            3:9
            4:9
            5:9
            6:9
            7:9
            8:9
            18:9
            10:9
            11:9
            12:9
            13:9
            14:9
            15:9
            16:9
            17:9
            9:9
            19:9
            """.ReplaceLineEndings("\n"), output);
    }
}
