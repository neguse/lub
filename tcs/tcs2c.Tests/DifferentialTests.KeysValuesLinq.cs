namespace TinyCs.Tcs2c.Tests;

// Dictionary の Keys / Values を TinySystem.Linq の受け手にする (List に写して List の版に渡す)。
// 列挙の順序は未規定なので、順序に依らない演算と OrderBy で並べた結果だけを見る。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void KeysValues_AreLinqReceivers()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    var hp = new Dictionary<string, int> { { "slime", 5 }, { "bat", 3 }, { "orc", 12 } };
                    var ids = new Dictionary<int, string> { { 7, "a" }, { 2, "b" }, { 9, "c" } };
                    Console.WriteLine(hp.Values.Sum() + " " + hp.Values.Min() + " " + hp.Values.Max()
                        + " " + hp.Values.Count(v => v > 4) + " " + (hp.Values.Any(v => v > 10) ? 1 : 0)
                        + " " + (hp.Values.All(v => v > 0) ? 1 : 0) + " " + hp.Values.Sum(v => v * 2));
                    Console.WriteLine(string.Join(",", hp.Keys.OrderBy(k => k)) + " "
                        + string.Join(",", hp.Keys.Where(k => k.Length == 3).OrderBy(k => k)) + " "
                        + hp.Keys.Max() + " " + hp.Keys.MaxBy(k => k.Length));
                    Console.WriteLine(ids.Keys.Sum(k => k) + " " + ids.Keys.Min() + " " + ids.Keys.Max()
                        + " " + string.Join(",", ids.Values.Select(v => v + "!").OrderByDescending(v => v))
                        + " " + ids.Keys.OrderBy(k => -k)[0]);
                }
            }
            """, "P");
        Assert.Equal("""
            20 3 12 2 1 1 40
            bat,orc,slime bat,orc slime slime
            18 2 9 c!,b!,a! 9
            """, output);
    }
}
