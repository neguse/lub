namespace TinyCs.Tcs2c.Tests;

// ToString(format) and interpolation formats of int / float produce the same strings as .NET for the standard formats.
// The expected values are the stdout of running the same program on CoreCLR
public class NumberToStringFormatDifferentialTests
{
    [CFact]
    public void NumberToStringFormat_MatchesDotnet()
    {
        const string source = """
            using System;
            public class P
            {
                public static void Main()
                {
                    float f = 1.5f;
                    int i = 255;
                    float? n = null;
                    float? m = 2.25f;
                    Console.WriteLine(f.ToString("F2") + "|" + f.ToString("E3") + "|" + f.ToString("N1") + "|" + f.ToString("G") + "|" + f.ToString(""));
                    Console.WriteLine(i.ToString("X4") + "|" + i.ToString("x") + "|" + i.ToString("D5") + "|" + i.ToString("N0") + "|" + i.ToString("F1"));
                    Console.WriteLine($"{f:E3}|{n:F1}|{m:F1}|{i:D5}|{-i:X}|{f:R}");
                }
            }
            """;
        Backends.AssertParity(source, "P");
    }
}
