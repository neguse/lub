namespace TinyCs.Tcs2c.Tests;

// Combinations of comparisons involving NaN (#110). GCC's jump threading at -O2 (threadfull1)
// derived a == b from `!(a < b) && !(a >= b)` and removed `!(a == b)` for
// NaN pairs. The expected values are the stdout of running the same program on CoreCLR (the pairs involving NaN are
// 2 + 64 + 128 + 256 = 450)
public class NaNRelationsDifferentialTests
{
    [CFact]
    public void Float_RelationsWithNaN_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static int Mask(float a, float b)
                {
                    int result = 0;
                    if (a == b) result += 1;
                    if (a != b) result += 2;
                    if (a < b) result += 4;
                    if (a <= b) result += 8;
                    if (a > b) result += 16;
                    if (a >= b) result += 32;
                    if (!(a < b)) result += 64;
                    if (!(a >= b)) result += 128;
                    if (!(a == b)) result += 256;
                    return result;
                }
                public static void Main()
                {
                    float zero = 0f; float nan = zero / zero; float inf = 1f / zero;
                    float[] xs = new float[] { nan, -inf, -2f, -zero, zero, 2f, inf };
                    foreach (float a in xs)
                    {
                        string line = "";
                        foreach (float b in xs) line += Mask(a, b) + " ";
                        Console.WriteLine(line);
                    }
                }
            }
            """, "P");
        Assert.Equal("""
            450 450 450 450 450 450 450 
            450 105 398 398 398 398 398 
            450 370 105 398 398 398 398 
            450 370 370 105 105 398 398 
            450 370 370 105 105 398 398 
            450 370 370 370 370 105 398 
            450 370 370 370 370 370 105 
            """.ReplaceLineEndings("\n").TrimEnd('\n'), output);
    }
}
