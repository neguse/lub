namespace TinyCs.Tcs2c.Tests;

// bool is converted to a string as `True` / `False`, same as .NET (Console.WriteLine, concatenation, interpolation, ToString,
// bool? (null is the empty string), string.Join elements). The expected values are the stdout of running the same program on CoreCLR
public class BoolToStringDifferentialTests
{
    [CFact]
    public void BoolToString_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public static class P
            {
                public static void Main()
                {
                    bool t = true;
                    bool f = false;
                    bool? n = null;
                    bool? nf = false;
                    Console.WriteLine(t);
                    Console.WriteLine(f + "|" + t.ToString() + "|" + $"{f}" + "|" + n + "|" + nf + "|" + $"{nf}");
                    Console.WriteLine(nf);
                    Console.WriteLine(string.Join(",", new List<bool> { true, false }));
                    var s = "x";
                    s += t;
                    Console.WriteLine(s);
                }
            }
            """, "P");
        Assert.Equal("""
            True
            False|True|False||False|False
            False
            True,False
            xTrue
            """, output);
    }

    // Console.Write (no newline) uses the same string conversion as WriteLine and writes no newline
    [CFact]
    public void ConsoleWrite_MatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    Console.Write("a");
                    Console.Write(1);
                    Console.Write(1.5f);
                    Console.Write(true);
                    Console.Write('c');
                    Console.WriteLine("|");
                }
            }
            """, "P");
        Assert.Equal("a11.5Truec|", output);
    }
}
