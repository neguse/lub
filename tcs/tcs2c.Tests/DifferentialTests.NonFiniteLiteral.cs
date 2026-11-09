namespace TinyCs.Tcs2c.Tests;

// float.NaN と ±Infinity の定数 (#408)。IL の literal を VM と C の backend が読めずに落ちていた
public partial class DifferentialTests
{
    [CFact]
    public void Float_NonFiniteConstants_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                const float Big = 3e38f * 10f;
                static float Neg() => float.NegativeInfinity;
                public static void Main()
                {
                    float x = float.NaN;
                    Console.WriteLine(x != x ? "nan" : "num");
                    float p = float.PositiveInfinity;
                    Console.WriteLine(p + "," + Neg() + "," + (p > 1e38f) + "," + (-p == Neg()));
                    Console.WriteLine(float.NaN + "," + Big + "," + (-Big) + "," + (Big == p));
                    float[] xs = new float[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, -float.NaN };
                    Console.WriteLine(xs[0] + "," + xs[1] + "," + xs[2] + "," + (xs[3] == xs[3]));
                }
            }
            """, "P");
        Assert.Equal("nan\nInfinity,-Infinity,True,True\nNaN,Infinity,-Infinity,True\nNaN,Infinity,-Infinity,False", output);
    }
}
