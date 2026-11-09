namespace TinyCs.Tcs2c.Tests;

// string.CompareOrdinal は 0 との比較だけで使い、符号は .NET の ordinal (UTF-16 の
// code unit の順、null は他のどの string より小さい) と同じ。UTF-8 の byte の順と
// UTF-16 の順が逆になる U+E000-U+FFFF と U+10000 以上の組、同じ上位 surrogate を
// 持つ組を含む。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
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
