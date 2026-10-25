namespace TinyCs.Tcs2c.Tests;

// List / 配列 / string の添字の範囲外は fault (il-spec §12)。Lua の table は範囲外の key を
// nil で読み、範囲外の key にも書けるので、Lua backend は添字ごとに範囲を見る。
// TinySystem.Linq の callback が受け手を縮めた場合も、範囲外の読み出しで止まる。
// 期待値は同じ program を CoreCLR で実行した stdout (どれも "before" の後に例外で停止する)
public partial class DifferentialTests
{
    [CTheory]
    [InlineData("var l = new List<int> { 1, 2 }; Console.WriteLine(l[2]);")]
    [InlineData("var l = new List<int> { 1, 2 }; Console.WriteLine(l[-1]);")]
    [InlineData("var l = new List<int> { 1, 2 }; l[2] = 5;")]
    [InlineData("var l = new List<string> { \"a\" }; l[1] = \"b\";")]
    [InlineData("var a = new int[2]; Console.WriteLine(a[2]);")]
    [InlineData("var a = new float[2]; a[-1] = 1f;")]
    [InlineData("var s = \"ab\"; Console.WriteLine((int)s[2]);")]
    [InlineData("var l = new List<int> { 1, 2, 3 }; l.Where(x => { l.RemoveAt(0); return true; });")]
    public void IndexOutOfRange_FaultsAfterPrecedingOutput(string body)
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
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    [CFact]
    public void IndexInRange_ReadsAndWrites()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    var l = new List<bool> { false, true };
                    l[0] = true;
                    var a = new int[3];
                    a[2] = 7;
                    a[0] += a[2];
                    var s = "xyz";
                    Console.WriteLine((l[0] ? 1 : 0) + ":" + a[0] + ":" + (int)s[2] + ":" + l.Count);
                }
            }
            """, "P");
        Assert.Equal("1:7:122:2", output);
    }
}
