namespace TinyCs.Tcs2c.Tests;

// NUL を含む string の幅付き補間は NUL の後ろも残す (#420)。C と tcs VM は NUL で切っていた
public partial class DifferentialTests
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
