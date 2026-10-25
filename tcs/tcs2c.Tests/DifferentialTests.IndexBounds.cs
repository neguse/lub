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
    [InlineData("var s = \"abc\"; Console.WriteLine((int)s[-2]);")]
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

    // 代入は受け手、添字、値の順に評価してから範囲を見る (C# の順序)
    [CFact]
    public void IndexStore_EvaluatesValueBeforeRangeFault()
    {
        Assert.Equal("""
            before
            G
            F
            F
            """, Backends.AssertFaultParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    var l = new List<int> { 1 };
                    Func<int> f = () => { Console.WriteLine("F"); return 2; };
                    Func<List<int>> g = () => { Console.WriteLine("G"); return l; };
                    Console.WriteLine("before");
                    g()[0] = f();
                    l[5] = f();
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // 型引数の要素 (T = bool の false) と、`?[]` で読む null の要素 / false の要素
    [CFact]
    public void IndexRead_FalseAndNullElementsThroughGenericAndConditional()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box<T>
            {
                public List<T> Items = new();
                public T Get(int i) => Items[i];
            }
            public static class P
            {
                public static void Main()
                {
                    var b = new Box<bool>();
                    b.Items.Add(false);
                    string?[]? names = new string?[2];
                    names[1] = "x";
                    List<bool>? flags = new List<bool> { false };
                    Console.WriteLine((b.Get(0) ? "T" : "F") + ":" + (names?[0] ?? "null0") + ":" + names?[1]
                        + ":" + (flags?[0] == false ? "F" : "T"));
                }
            }
            """, "P");
        Assert.Equal("F:null0:x:F", output);
    }

    // 値の呼び出しが受け手や添字の変数を書き換えても、書き込む先は値より先に評価した受け手と添字
    [CFact]
    public void IndexStore_ValueCallChangingReceiverOrIndex()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                static int si = 0;
                static List<int> a = new() { 1, 2, 3 };
                static int F() { si = 2; return 9; }
                static int G() { a = new() { 7, 7, 7 }; return 5; }
                public static void Main()
                {
                    a[si] = F();
                    Console.WriteLine(a[0] + "," + a[2]);
                    var old = a;
                    a[0] = G();
                    Console.WriteLine(old[0] + "," + a[0]);
                }
            }
            """, "P");
        Assert.Equal("""
            9,3
            5,7
            """, output);
    }
}
