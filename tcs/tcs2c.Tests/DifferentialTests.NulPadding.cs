namespace TinyCs.Tcs2c.Tests;

// Width-padded interpolation of a string containing NUL keeps what follows the NUL (#420). C and the tcs VM cut it at the NUL
public class NulPaddingDifferentialTests
{
    [CFact]
    public void Interpolation_WidthKeepsTextAfterNul()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    string s = "a\0b";
                    Console.WriteLine($"[{s,6}]".Replace("\0", "!"));
                    Console.WriteLine($"[{s,-6}]".Replace("\0", "!"));
                    Console.WriteLine($"[{s,2}][{"\0",3}]".Replace("\0", "!"));
                }
            }
            """, "P");
        Assert.Equal("[   a!b]\n[a!b   ]\n[a!b][  !]", output);
    }
}
