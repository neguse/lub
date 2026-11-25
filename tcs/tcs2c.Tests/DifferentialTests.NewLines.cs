namespace TinyCs.Tcs2c.Tests;

// Lexing of source saved with C# newlines (LF, CRLF, CR, U+0085, U+2028, U+2029).
// The newlines of the same program are replaced with each of them and run. A // comment and a directive line end at the newline,
// a multi-line raw string removes the newline after the opening, the newline before the closing, and the indentation, and keeps the newlines of the content as written, and
// whether it is single-line or multi-line is decided only by what follows the opening delimiter. A } in a comment inside an interpolation hole does not close the hole.
// ~ in the program is a space (so that the test file has no trailing whitespace). The expected value is the stdout of the same program run on CoreCLR
public class NewLinesDifferentialTests
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

    // The source with its newlines turned into kind, and the newline mark that Show of the program prints
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
