namespace TinyCs.Tcs2c.Tests;

// NaN を含む比較の組み合わせ (#110)。GCC は -O2 の jump threading (threadfull1)
// で `!(a < b) && !(a >= b)` から a == b を導き、NaN の組で `!(a == b)` を
// 消していた。期待値は同じ program を CoreCLR で実行した stdout (NaN を含む組は
// 2 + 64 + 128 + 256 = 450)
public partial class DifferentialTests
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
