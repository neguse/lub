namespace TinyCs.Tcs2c.Tests;

// C# の改行 (LF、CRLF、CR、U+0085、U+2028、U+2029) で保存した source の字句。
// 同じ program の改行をそれぞれに置き換えて動かす。// のコメントと指令の行は改行で終わり、
// 複数行の raw 文字列は開きの後と閉じの前の改行と字下げを除いて、中身の改行を書いたまま残し、
// 1 行か複数行かは開きの区切りの後ろだけで決まる。補間の穴の中のコメントの } は穴を閉じない。
// program の中の ~ は空白 (行末の空白を test の file に置かないため)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    private static readonly Dictionary<string, string> NewLineKinds = new()
    {
        ["LF"] = "\n",
        ["CRLF"] = "\r\n",
        ["CR"] = "\r",
        ["NEL"] = "\u0085",
        ["LS"] = "\u2028",
        ["PS"] = "\u2029",
    };

    // 改行を kind にした source と、program の Show が出す改行の印
    private static (string Source, string Mark) WithNewLines(string source, string kind)
    {
        var nl = NewLineKinds[kind];
        var mark = nl.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\u0085", "<NEL>")
            .Replace("\u2028", "<LS>").Replace("\u2029", "<PS>");
        return (source.ReplaceLineEndings("\n").Replace("~", " ").Replace("\n", nl), mark);
    }

    private const string ShowNewLines =
        """static string Show(string s) => s.Replace("\r", "<CR>").Replace("\n", "<LF>").Replace("\u0085", "<NEL>").Replace("\u2028", "<LS>").Replace("\u2029", "<PS>");""";

    [CTheory]
    [InlineData("LF")]
    [InlineData("CRLF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void NewLines_EndLineCommentsAndDirectives(string kind)
    {
        var (source, _) = WithNewLines("""
            using System;
            public class P
            {
                public static void Main()
                {
                    string s = "a";
                    // comment
                    s += "b";
                    s += "c"; // trailing comment
                    s += "d";
            #if false
                    s += "x";
            #endif
                    s += "e"; /* block */ s += "f";
                    Console.WriteLine(s);
                }
            }
            """, kind);
        var output = Backends.AssertParity(source, "P");
        Assert.Equal("abcdef", output);
    }

    [CTheory]
    [InlineData("LF")]
    [InlineData("CRLF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void NewLines_RawStringKeepsContentNewLines(string kind)
    {
        var (source, mark) = WithNewLines(""""
            using System;
            public class P
            {
                SHOW
                public static void Main()
                {
                    int n = 1;
                    string a = """~~
                        A
                          B

                        C
                        """;
                    Console.WriteLine(Show(a));
                    string b = $"""~~
                        A{n}
                    ~~
                    ~~~~~~~~
                        B
                        """;
                    Console.WriteLine(Show(b));
                    string c = $"""

                        C{n}
                        """;
                    Console.WriteLine(Show(c));
                    string d = $"""
                    ~~
                        D{n}
                        """;
                    Console.WriteLine(Show(d));
                    string e = """

                        E
                        """;
                    Console.WriteLine(Show(e));
                    string f = """
                    F
                    """;
                    Console.WriteLine(Show(f) + "|" + Show($"""
                    {n}
                    """));
                }
            }
            """".Replace("SHOW", ShowNewLines), kind);
        var output = Backends.AssertParity(source, "P");
        Assert.Equal(string.Join("\n",
            "A<NL>  B<NL><NL>C",
            "A1<NL><NL>    <NL>B",
            "<NL>C1",
            "<NL>D1",
            "<NL>E",
            "F|1").Replace("<NL>", mark), output);
    }

    [CTheory]
    [InlineData("LF")]
    [InlineData("CRLF")]
    [InlineData("CR")]
    [InlineData("NEL")]
    [InlineData("LS")]
    [InlineData("PS")]
    public void NewLines_InterpolationHoleSpansLines(string kind)
    {
        var (source, mark) = WithNewLines(""""
            using System;
            public class P
            {
                SHOW
                public static void Main()
                {
                    float f = 1.5f;
                    string a = $"""A{(
                        1 +
                        2
                        )}Z""";
                    Console.WriteLine(Show(a));
                    string b = $"""
                        {1 // }
                        + 2}
                        """;
                    Console.WriteLine(Show(b));
                    string c = $$"""
                        {{1 // }}
                        + 2}}
                        """;
                    Console.WriteLine(Show(c));
                    string d = $@"{1 // }
                        + 2}";
                    Console.WriteLine(Show(d));
                    string e = $"{1 // it's "
                        + 2}";
                    Console.WriteLine(Show(e));
                    string g = $"""{1 /* } */ + 2}|{"a//b}:c"}|{'}'}|{f // :F1 }
                        :F2}""";
                    Console.WriteLine(Show(g));
                    string h = $"""
                        A{(
                    1 +
                    2)}Z
                        B
                        """;
                    Console.WriteLine(Show(h));
                }
            }
            """".Replace("SHOW", ShowNewLines), kind);
        var output = Backends.AssertParity(source, "P");
        Assert.Equal(string.Join("\n",
            "A3Z",
            "3",
            "3",
            "3",
            "3",
            "3|a//b}:c|}|1.50",
            "A3Z<NL>B").Replace("<NL>", mark), output);
    }
}
