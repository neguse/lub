namespace TinyCs.Tcs2c.Tests;

// uint の literal (16 進と 2 進の suffix つきも) と、int への cast で受ける bit の並び。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
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
