namespace TinyCs.Tests;

// Diagnostics for C# newlines (LF, CRLF, CR, U+0085, U+2028, U+2029). The line of a diagnostic counts every newline, as Roslyn does, and
// a newline in a regular string and in the literal part of an interpolated string is CS1010
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

    // A // comment in a hole runs to the end of the line and includes the closing } on the same line. An unclosed hole is CS8076 without crashing the compiler
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
