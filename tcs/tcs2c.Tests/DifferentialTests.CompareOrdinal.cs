namespace TinyCs.Tcs2c.Tests;

// string.CompareOrdinal is used only in comparisons with 0, and its sign matches .NET's ordinal (UTF-16
// code unit order, null is less than any other string). Includes pairs where UTF-8 byte order and
// UTF-16 order are reversed (U+E000-U+FFFF against U+10000 and above), and pairs that have the same high surrogate.
// The expected values are the stdout of running the same program on CoreCLR
public class CompareOrdinalDifferentialTests
{
    [CFact]
    public void CompareOrdinal_SignMatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static void Show(string? a, string? b)
                {
                    var lt = string.CompareOrdinal(a, b) < 0;
                    var eq = string.CompareOrdinal(a, b) == 0;
                    var gt = 0 < string.CompareOrdinal(a, b);
                    Console.WriteLine((lt ? "<" : "") + (eq ? "=" : "") + (gt ? ">" : ""));
                }
                public static void Main()
                {
                    Show("a", "c");
                    Show("abc", "ab");
                    Show("", "x");
                    Show("x", "");
                    Show("abc", "abc");
                    Show("A", "a");
                    Show("é", "e");
                    Show("aé", "aê");
                    Show("", "😀");
                    Show("😀", "￿");
                    Show("😀", "😁");
                    Show("x😀", "x");
                    Show(null, "a");
                    Show("a", null);
                    Show(null, null);
                    if (string.CompareOrdinal("b", "a") >= 0) Console.WriteLine("ge");
                    if (string.CompareOrdinal("b", "a") != 0) Console.WriteLine("ne");
                }
            }
            """, "P");
        Assert.Equal("""
            <
            >
            <
            >
            =
            <
            >
            <
            >
            <
            <
            >
            <
            >
            =
            ge
            ne
            """, output);
    }
}
