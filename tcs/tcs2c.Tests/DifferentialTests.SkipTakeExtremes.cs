namespace TinyCs.Tcs2c.Tests;

// Skip / Take の count が int の端 (int.MaxValue / int.MinValue) でも要素数で
// 打ち切る。LUA_32BITS の runtime で開始位置の count + 1 が wrap しないこと。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Linq_SkipTakeIntExtremes_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using System.Linq;
            public class P
            {
                public static string Show(List<int> l)
                {
                    string s = l.Count + ":";
                    for (int i = 0; i < l.Count; i++) s += l[i] + ",";
                    return s;
                }
                public static void Main()
                {
                    int max = int.MaxValue;
                    var one = new List<int> { 7 };
                    var three = new List<int> { 1, 2, 3 };
                    var empty = new List<int>();
                    Console.WriteLine("before");
                    Console.WriteLine(one.Skip(max).ToList().Count);
                    Console.WriteLine(Show(three.Skip(max).ToList()) + " " + Show(empty.Skip(max).ToList()) + " " + Show(three.Skip(max - 1).ToList()));
                    Console.WriteLine(Show(three.Skip(3).ToList()) + " " + Show(three.Skip(2).ToList()) + " " + Show(three.Skip(0).ToList()) + " " + Show(three.Skip(-1).ToList()) + " " + Show(three.Skip(-2147483647 - 1).ToList()));
                    Console.WriteLine(Show(three.Take(max).ToList()) + " " + Show(three.Take(-1).ToList()) + " " + Show(three.Skip(1).Take(max).ToList()) + " " + Show(three.Take(max).Skip(max).ToList()));
                    Console.WriteLine("after");
                }
            }
            """, "P");
        Assert.Equal("""
            before
            0
            0: 0: 0:
            0: 1:3, 3:1,2,3, 3:1,2,3, 3:1,2,3,
            3:1,2,3, 0: 2:2,3, 0:
            after
            """.ReplaceLineEndings("\n"), output);
    }
}
