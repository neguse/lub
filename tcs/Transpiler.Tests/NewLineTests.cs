namespace TinyCs.Tests;

// C# の改行 (LF、CRLF、CR、U+0085、U+2028、U+2029) の診断。診断の行は Roslyn と同じくどの改行でも数え、
// 通常の文字列と補間文字列の文字の部分の改行は CS1010
public class NewLineTests
{
    public static TheoryData<string> NewLines => ["\n", "\r\n", "\r", "\u0085", "\u2028", "\u2029"];

    [Theory]
    [MemberData(nameof(NewLines))]
    public void Diagnostics_CountLinesAtEveryNewLine(string nl)
    {
        var source = """
            public class P
            {
                // comment
                public static void Main()
                {
                    long x = 1;
                }
            }
            """.ReplaceLineEndings("\n").Replace("\n", nl);
        var result = Checker.Check([source], ["A.cs"]);
        Assert.Contains(result.Errors, e => e.StartsWith("A.cs(6,9): error TCS1001", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(NewLines))]
    public void RegularStringAndInterpolation_NewLineIsError(string nl)
    {
        foreach (var literal in new[] { "\"A" + nl + "B\"", "$\"A" + nl + "{1}\"" })
        {
            var result = Checker.Check(["public class P { public static string S = " + literal + "; }"]);
            Assert.Contains(result.Errors, e => e.Contains("error CS1010", StringComparison.Ordinal));
        }
    }

    // 穴の // のコメントは行の終わりまで続き、同じ行の閉じの } を含む。閉じない穴は compiler を落とさず CS8076
    [Theory]
    [InlineData("$\"\"\"{1 // }\"\"\"")]
    [InlineData("$\"\"\"{1\"\"\"")]
    [InlineData("$\"{1 // }\"")]
    [InlineData("$@\"{1 // }\"")]
    public void UnclosedHole_IsError(string literal)
    {
        var result = Checker.Check(["public class P { public static string S = " + literal + "; }"]);
        Assert.Contains(result.Errors, e => e.Contains("error CS8076", StringComparison.Ordinal));
    }
}
