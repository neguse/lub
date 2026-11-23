namespace TinyCs.Tcs2c.Tests;

// uint literals (including hex and binary with suffixes) and the bit patterns received through a cast to int.
// The expected values are the stdout of running the same program on CoreCLR
public class UIntLiteralDifferentialTests
{
    [CFact]
    public void UIntLiteral_SuffixAndBitPattern()
    {
        const string source = """
            using System;
            public class P
            {
                public static void Main()
                {
                    uint x = 0xFFu;
                    uint y = 0b101U;
                    uint z = 7u;
                    int c = unchecked((int)0xFFFFFFFF);
                    uint w = (x & 0xF0u) | (z >> 1) ^ (x / 3u) % 7u;
                    Console.WriteLine(x + " " + y + " " + z + " " + c + " " + w + " " + (x > z) + " " + 0x1Fu);
                }
            }
            """;
        Backends.AssertParity(source, "P");
    }
}
