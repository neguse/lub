namespace TinyCs.Tcs2c.Tests;

// null の string の補間と Console の出力は空文字列 (#415)。C は `nil` を書いていた
public partial class DifferentialTests
{
    [CFact]
    public void NullString_InterpolationAndConsole_AreEmpty()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static string Nil() => null;
                public static void Main()
                {
                    string value = null;
                    Console.WriteLine($"[{value}]");
                    Console.WriteLine($"[{Nil()}][{value,4}][{value,-4}]");
                    Console.WriteLine(value);
                    Console.Write(value);
                    Console.WriteLine("|");
                    Console.WriteLine("[" + value + "]");
                }
            }
            """, "P");
        Assert.Equal("[]\n[][    ][    ]\n\n|\n[]", output);
    }
}
