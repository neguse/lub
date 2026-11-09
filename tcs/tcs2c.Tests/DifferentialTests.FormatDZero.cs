namespace TinyCs.Tcs2c.Tests;

// 補間の D 精度 0 (と 0 を表す精度)。C# は値 0 でも最低 1 桁を出すが、
// printf の %.0d は値 0 で数字を出さない。期待値は同じ program を CoreCLR
// で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Interpolation_DZeroPrecision_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    int zero = 0;
                    int neg = -12;
                    int one = 1;
                    int min = -2147483647 - 1;
                    Console.WriteLine($"{zero:D0}|{zero:D1}|{zero:D00}|{zero:D2}|{neg:D0}|{neg:D1}|{one:D0}|{min:D0}|{zero,3:D0}|{zero:d0}|{0:D0}");
                }
            }
            """, "P");
        Assert.Equal("0|0|0|00|-12|-12|1|-2147483648|  0|0|0", output);
    }
}
