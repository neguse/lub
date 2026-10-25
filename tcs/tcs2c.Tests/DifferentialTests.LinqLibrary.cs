namespace TinyCs.Tcs2c.Tests;

// TinySystem.Linq (TinySystem/Linq.cs) を user のコードと一緒に Lua / C へ変換する。
// callback が受け手に足しても走査は呼び出した時点の Count まで、空の Min / Max は fault。
// 期待値は同じ program を CoreCLR で実行した stdout (空の Min は "before" の後に
// InvalidOperationException で停止する)
public partial class DifferentialTests
{
    [CFact]
    public void LinqLibrary_IteratesUpToCountAtCall()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    var list = new List<int> { 3, 1, 2 };
                    var kept = list.Where(x => { list.Add(x * 10); return x > 1; });
                    var keys = "";
                    var sorted = list.OrderBy(x => { keys += x + ","; return -x; });
                    Console.WriteLine(kept.Count + " " + list.Count + " " + sorted[0] + " " + keys);
                }
            }
            """, "P");
        Assert.Equal("2 6 30 3,1,2,30,10,20,", output);
    }

    [CTheory]
    [InlineData("new List<int>().Min()")]
    [InlineData("new List<float>().Max()")]
    [InlineData("new List<string>().Max(s => s.Length)")]
    public void LinqLibrary_MinMaxOnEmpty_FaultsAfterPrecedingOutput(string call)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine({{call}});
                }
            }
            """, "P"));
    }
}
