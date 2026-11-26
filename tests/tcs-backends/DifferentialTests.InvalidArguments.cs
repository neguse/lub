namespace TinyCs.Tcs2c.Tests;

// Math and Random calls whose arguments .NET rejects with an exception fault (il-spec §12). All arguments are evaluated before the check.
// The expected value is the stdout of the same program run on CoreCLR (each stops with an exception after "before")
public class InvalidArgumentsDifferentialTests
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

    // Boundary values and values .NET does not reject (a NaN range, infinities, -0, a range of equal values)
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

    [CTheory]
    [InlineData("r.Next(int.MinValue)")]
    [InlineData("r.Next(0, int.MinValue)")]
    [InlineData("r.Next(int.MaxValue, int.MinValue)")]
    [InlineData("r.Next(-1)")]
    [InlineData("r.Next(int.MinValue + 1)")]
    [InlineData("r.Next(5, 0)")]
    [InlineData("r.Next(1, 0)")]
    public void RandomNextInvalidBound_FaultsAfterPrecedingOutput(string expr)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            using Random = TinySystem.Random;
            public static class P
            {
                public static void Main()
                {
                    Random r = new Random(1);
                    Console.WriteLine("before");
                    Console.WriteLine({{expr}});
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // A range of width 0 or 1 fixes the value. For the others only being in range is checked (the random sequence differs from .NET)
    [CFact]
    public void RandomNextBoundary_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using Random = TinySystem.Random;
            public static class P
            {
                public static void Main()
                {
                    Random r = new Random(7);
                    Console.WriteLine(r.Next(0) + "," + r.Next(1) + "," + r.Next(5, 5) + "," + r.Next(-3, -2));
                    Console.WriteLine(r.Next(int.MinValue, int.MinValue) + "," + r.Next(int.MaxValue, int.MaxValue));
                    Console.WriteLine(r.Next(int.MinValue, int.MinValue + 1) + "," + r.Next(int.MaxValue - 1, int.MaxValue));
                    bool inRange = true;
                    for (int i = 0; i < 64; i++)
                    {
                        int a = r.Next(int.MinValue, int.MaxValue);
                        int b = r.Next(-5, 5);
                        int c = r.Next(int.MaxValue);
                        inRange = inRange && a < int.MaxValue && b >= -5 && b < 5 && c >= 0 && c < int.MaxValue;
                    }
                    Console.WriteLine(inRange);
                }
            }
            """, "P");
        Assert.Equal("0,0,5,-3\n-2147483648,2147483647\n-2147483648,2147483646\nTrue", output);
    }
}
