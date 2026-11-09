namespace TinyCs.Tcs2c.Tests;

// checked(expr) の i32 の + - * と単項 - は overflow で fault (C# の OverflowException)。
// 値は parameter から渡して定数畳み込みを避ける。期待値は同じ program を CoreCLR で
// 実行した stdout (overflow は "before" の後に OverflowException で停止する)
public partial class DifferentialTests
{
    private static string CheckedProgram(string expr) => $$"""
        using System;
        public class P
        {
            static int Eval(int a, int b) { return {{expr}}; }
            public static void Main()
            {
                Console.WriteLine("before");
                Console.WriteLine(Eval(Pick(0), Pick(1)));
            }
            static int Pick(int i) { return Args[i]; }
            static int[] Args = new int[] { A, B };
            const int A = __A__;
            const int B = __B__;
        }
        """;

    private static string Checked(string expr, string a, string b) =>
        CheckedProgram(expr).Replace("__A__", a).Replace("__B__", b);

    [CTheory]
    [InlineData("checked(a + b)", "2147483647", "1")]
    [InlineData("checked(a + b)", "-2147483647 - 1", "-1")]
    [InlineData("checked(a - b)", "-2147483647 - 1", "1")]
    [InlineData("checked(a - b)", "2147483647", "-1")]
    [InlineData("checked(a * b)", "65536", "65536")]
    [InlineData("checked(a * b)", "-2147483647 - 1", "-1")]
    [InlineData("checked(a * b)", "-1", "-2147483647 - 1")]
    [InlineData("checked(a * b)", "46341", "-46341")]
    [InlineData("checked(-a + b * 0)", "-2147483647 - 1", "0")]
    public void CheckedInt_Overflow_FaultsAfterPrecedingOutput(string expr, string a, string b)
    {
        Assert.Equal("before", Backends.AssertFaultParity(Checked(expr, a, b), "P"));
    }

    [CTheory]
    [InlineData("checked(a + b)", "2147483646", "1", "2147483647")]
    [InlineData("checked(a + b)", "-2147483647", "-1", "-2147483648")]
    [InlineData("checked(a - b)", "-2147483647", "1", "-2147483648")]
    [InlineData("checked(a * b)", "46340", "46340", "2147395600")]
    [InlineData("checked(a * b)", "-65536", "32768", "-2147483648")]
    [InlineData("checked(a * b)", "-1", "2147483647", "-2147483647")]
    [InlineData("checked(a * b)", "0", "-2147483647 - 1", "0")]
    [InlineData("checked(-a)", "2147483647", "0", "-2147483647")]
    [InlineData("checked(a + b * 2) + unchecked(a + b)", "1", "2", "8")]
    [InlineData("checked(a / b)", "7", "-2", "-3")]
    public void CheckedInt_WithinRange_MatchesDotnet(string expr, string a, string b,
        string expected)
    {
        Assert.Equal("before\n" + expected,
            Backends.AssertParity(Checked(expr, a, b), "P"));
    }
}
