namespace TinyCs.Tcs2c.Tests;

// 既定の順序 (比較なしの List.Sort() と OrderBy / OrderByDescending) は
// .NET の既定の comparer と同じく NaN を最小にする。要素型が型 parameter の
// generic method と、拡張 method の static の形も同じ。
// 表示の N は NaN、I は ±∞。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void DefaultOrder_NaNFirst_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class Item
            {
                public int Id;
                public float Key;
            }
            public class P
            {
                public static void GenSort<T>(List<T> l) { l.Sort(); }
                public static string Show(List<float> values)
                {
                    string result = "";
                    for (int i = 0; i < values.Count; i++)
                    {
                        float v = values[i];
                        result += (v != v ? "N" : v > 1000000.0f ? "I" : v < -1000000.0f ? "-I" : ((int)v).ToString()) + ",";
                    }
                    return result;
                }
                public static string Ids(List<Item> items)
                {
                    string result = "";
                    for (int i = 0; i < items.Count; i++) result += items[i].Id + ",";
                    return result;
                }
                public static void Main()
                {
                    float zero = 0.0f;
                    float nan = zero / zero;
                    float inf = 1.0f / zero;
                    var values = new List<float> { 2.0f, nan, 1.0f };
                    values.Sort();
                    Console.WriteLine(Show(values));
                    var mixed = new List<float> { 3.0f, nan, -inf, 1.0f, nan, inf, -2.0f, nan };
                    mixed.Sort();
                    Console.WriteLine(Show(mixed));
                    var big = new List<float>();
                    for (int i = 0; i < 40; i++) big.Add(i % 7 == 3 ? nan : (float)((i * 17) % 23 - 11));
                    big.Sort();
                    Console.WriteLine(Show(big));
                    var allNan = new List<float> { nan, nan };
                    allNan.Sort();
                    Console.WriteLine(Show(allNan));
                    var source = new List<float> { 2.0f, nan, 1.0f };
                    Console.WriteLine(Show(source.OrderBy(x => x)) + " " + Show(source.OrderByDescending(x => x)) + " " + Show(source));
                    Console.WriteLine(Show(mixed.OrderBy(x => -x)) + " " + Show(mixed.OrderByDescending(x => x)));
                    var finite = new List<float> { 2.0f, 0.0f, 1.0f };
                    Console.WriteLine(Show(finite.OrderBy(x => 1.0f)) + " " + Show(finite.OrderByDescending(x => 1.0f)) + " " + Show(finite.OrderByDescending(x => x)));
                    var items = new List<Item>();
                    for (int i = 0; i < 9; i++) items.Add(new Item { Id = i, Key = i % 3 == 0 ? nan : (float)(i % 4) });
                    Console.WriteLine(Ids(items.OrderBy(it => it.Key)) + " " + Ids(items.OrderByDescending(it => it.Key)));
                    var g = new List<float> { 2.0f, nan, 1.0f };
                    GenSort(g);
                    var f = new List<float> { 2.0f, nan, 1.0f };
                    TinySystem.List.Sort(f);
                    Console.WriteLine(Show(g) + " " + Show(f) + " " + Show(source.OrderBy(x => x)) + " " + Show(source.OrderByDescending(x => x)) + " " + Show(Enumerable.OrderBy(source, x => x)) + " " + Show(Enumerable.OrderByDescending(source, x => x)));
                    Console.WriteLine(Show(source.OrderBy(x => -x)) + " " + Show(mixed.OrderByDescending(x => x)));
                    var ints = new List<int> { 3, -1, 2 };
                    ints.Sort();
                    var words = new List<string> { "b", "a", "c" };
                    words.Sort();
                    Console.WriteLine(ints[0] + "," + ints[1] + "," + ints[2] + " " + words[0] + words[1] + words[2] + " " + Ids(items.OrderBy(it => it.Id % 2)));
                }
            }
            """, "P");
        Assert.Equal("""
            N,1,2,
            N,N,N,-I,-2,1,3,I,
            N,N,N,N,N,N,-11,-11,-10,-9,-8,-8,-7,-7,-6,-5,-4,-3,-3,-2,-1,-1,0,0,1,3,3,4,4,5,5,6,7,8,8,9,9,10,11,11,
            N,N,
            N,1,2, 2,1,N, 2,N,1,
            N,N,N,I,3,1,-2,-I, I,3,1,-2,-I,N,N,N,
            2,0,1, 2,0,1, 2,1,0,
            0,3,6,4,8,1,5,2,7, 7,2,1,5,4,8,0,3,6,
            N,1,2, N,1,2, N,1,2, 2,1,N, N,1,2, 2,1,N,
            N,2,1, I,3,1,-2,-I,N,N,N,
            -1,2,3 abc 0,2,4,6,8,1,3,5,7,
            """.ReplaceLineEndings("\n"), output);
    }
}
