namespace TinyCs.Tcs2c.Tests;

// A unary minus whose operand starts with `-` does not become `--` (a line comment in Lua, the decrement operator in C).
// The expected values are the stdout of running the same program on CoreCLR
public class UnaryMinusDifferentialTests
{
    [CFact]
    public void UnaryMinus_OperandStartingWithMinus()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                const int K = -5;
                const float FK = -2.5f;
                static int Neg() { return -K; }
                public static void Main()
                {
                    int x = 3;
                    int y = - -x;
                    Console.WriteLine(y);
                    Console.WriteLine(Neg());
                    Console.WriteLine(-(-5));
                    Console.WriteLine(-(-x));
                    Console.WriteLine(- - -x);
                    Console.WriteLine(-FK);
                    Console.WriteLine(1 - -x);
                    Console.WriteLine(1 - K);
                    Console.WriteLine(- -(x + 1));
                    float f = 1.5f;
                    Console.WriteLine(- -f);
                }
            }
            """, "P");
        Assert.Equal("3\n5\n5\n3\n-3\n2.5\n4\n6\n4\n1.5", output);
    }
}
