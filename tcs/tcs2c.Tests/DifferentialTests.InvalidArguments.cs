namespace TinyCs.Tcs2c.Tests;

// .NET が引数を拒んで例外を投げる Math の呼び出しは fault (il-spec §12)。引数は全て評価してから検査する。
// 期待値は同じ program を CoreCLR で実行した stdout (どれも "before" の後に例外で停止する)
public partial class DifferentialTests
{
    [CTheory]
    [InlineData("Math.Clamp(5, 10, 0)")]
    [InlineData("Math.Clamp(5f, 10f, 0f)")]
    [InlineData("Math.Clamp(5, 10f, 0f)")]
    [InlineData("Math.Clamp(z, z + 1, z)")]
    [InlineData("Math.Clamp(f, f, f - 1f)")]
    [InlineData("Math.Abs(int.MinValue)")]
    [InlineData("Math.Abs(z - 2147483647 - 1)")]
    [InlineData("Math.Sign(float.NaN)")]
    [InlineData("Math.Sign(f / f)")]
    [InlineData("Math.Round(1.25f, -1)")]
    public void MathInvalidArgument_FaultsAfterPrecedingOutput(string expr)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            public static class P
            {
                public static void Main()
                {
                    int z = 0;
                    float f = 0f;
                    Console.WriteLine("before");
                    Console.WriteLine({{expr}});
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    [CFact]
    public void MathClampInvalidRange_FaultsAfterEvaluatingArguments()
    {
        Assert.Equal("before\n5\n10\n0", Backends.AssertFaultParity("""
            using System;
            public static class P
            {
                static int Log(int v) { Console.WriteLine(v); return v; }
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine(Math.Clamp(Log(5), Log(10), Log(0)));
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // 境界の値と、.NET が拒まない値 (NaN の範囲、無限、-0、同じ値の範囲)
    [CFact]
    public void MathValidArgument_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    float nan = float.NaN;
                    Console.WriteLine(Math.Clamp(5, 5, 5) + "," + Math.Clamp(-1, 0, 10) + "," + Math.Clamp(11, 0, 10) + "," + Math.Clamp(3, 0, 10));
                    Console.WriteLine(Math.Clamp(nan, 0f, 1f) + "," + Math.Clamp(2f, nan, 1f) + "," + Math.Clamp(-2f, 0f, nan) + "," + Math.Clamp(0.5f, 0.5f, 0.5f));
                    Console.WriteLine(Math.Abs(int.MinValue + 1) + "," + Math.Abs(int.MaxValue) + "," + Math.Abs(-7) + "," + Math.Abs(0));
                    Console.WriteLine(Math.Sign(float.PositiveInfinity) + "," + Math.Sign(float.NegativeInfinity) + "," + Math.Sign(-0f) + "," + Math.Sign(int.MinValue));
                }
            }
            """, "P");
        Assert.Equal("5,0,10,3\nNaN,1,0,0.5\n2147483647,2147483647,7,0\n1,-1,0,-1", output);
    }
}
