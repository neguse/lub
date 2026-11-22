namespace TinyCs.Tcs2c.Tests;

// D precision 0 in interpolation (and precisions that mean 0). C# prints at least one digit even for the value 0, but
// printf's %.0d prints no digits for the value 0. The expected values are the stdout of running the same program
// on CoreCLR
public class FormatDZeroDifferentialTests
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
