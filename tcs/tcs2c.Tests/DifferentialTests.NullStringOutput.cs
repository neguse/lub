namespace TinyCs.Tcs2c.Tests;

// Interpolation and Console output of a null string give the empty string (#415). C used to write `nil`
public class NullStringOutputDifferentialTests
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
