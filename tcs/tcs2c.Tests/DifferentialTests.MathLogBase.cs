namespace TinyCs.Tcs2c.Tests;

// Special values of TinySystem.Math.Log(x, base) (same as System.MathF.Log): NaN when base is 1,
// or when x is not 1 and base is 0 / +∞. If x is 1, base 0 / +∞ gives -0 / +0.
// In the table, rows are x and columns are base (the last column is the one-argument Log). 9=NaN, -1=-0, 0=+0,
// ±8=±∞, others are truncated with (int). The expected values are the stdout of running the same program on CoreCLR
public class MathLogBaseDifferentialTests
{
    [CFact]
    public void MathLog_BaseSpecialValues_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using M = TinySystem.Math;
            public class P
            {
                public static int Classify(float x)
                {
                    if (x != x) return 9;
                    if (x == 0.0f) return 1.0f / x < 0.0f ? -1 : 0;
                    if (x > 1000000.0f) return 8;
                    if (x < -1000000.0f) return -8;
                    return (int)x;
                }
                public static void Main()
                {
                    float zero = 0.0f;
                    float negZero = -zero;
                    float inf = 1.0f / zero;
                    float nan = zero / zero;
                    var xs = new List<float> { 2.0f, 1.0f, zero, -1.0f, inf, nan, 8.0f, 0.5f };
                    var bases = new List<float> { 1.0f, zero, negZero, inf, -inf, nan, 2.0f, 10.0f, 0.5f, -2.0f };
                    for (int i = 0; i < xs.Count; i++)
                    {
                        string line = "";
                        for (int j = 0; j < bases.Count; j++)
                            line += Classify(M.Log(xs[i], bases[j])) + " ";
                        Console.WriteLine(line + Classify(M.Log(xs[i])));
                    }
                    Console.WriteLine(Classify(M.Log(100.0f, 10.0f)) + " " + Classify(M.Log(4.0f, 0.5f)) + " " + Classify(M.Log(2.0f, 1.0f)) + " " + Classify(M.Log(1.0f, inf)));
                }
            }
            """, "P");
        Assert.Equal("""
            9 9 9 9 9 9 1 0 -1 9 0
            9 -1 -1 0 9 9 0 0 -1 9 0
            9 9 9 9 9 9 -8 -8 8 9 -8
            9 9 9 9 9 9 9 9 9 9 9
            9 9 9 9 9 9 8 8 -8 9 8
            9 9 9 9 9 9 9 9 9 9 9
            9 9 9 9 9 9 3 0 -3 9 2
            9 9 9 9 9 9 -1 0 1 9 0
            2 -2 9 0
            """.ReplaceLineEndings("\n"), output);
    }
}
