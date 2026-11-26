namespace TinyCs.Tcs2c.Tests;

// i32 + - * and unary - in checked(expr) fault on overflow (C#'s OverflowException).
// Values are passed through parameters to avoid constant folding. The expected values are the stdout of running the same program
// on CoreCLR (overflow stops with OverflowException after "before")
public class CheckedIntDifferentialTests
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
