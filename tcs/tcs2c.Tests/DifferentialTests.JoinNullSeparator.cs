namespace TinyCs.Tcs2c.Tests;

// A null separator of string.Join is the empty string (same as .NET). It is the same for every shape: a List / an array / value-type elements / enum elements / listed arguments.
// The expected value is the stdout of the same program run on CoreCLR
public class JoinNullSeparatorDifferentialTests
{
    [CFact]
    public void StringJoin_NullSeparator_IsEmpty()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum Color { Red, Green }
            public static class P
            {
                static string Sep() => null;
                public static void Main()
                {
                    string separator = null;
                    Console.WriteLine(string.Join(separator, new string[] { "a", "b" }));
                    Console.WriteLine(string.Join(separator, new List<string> { "c", null, "d" }));
                    Console.WriteLine(string.Join(Sep(), new int[] { 1, 2, 3 }));
                    Console.WriteLine(string.Join(separator, new List<float> { 1.5f, 2f }));
                    Console.WriteLine(string.Join(separator, new List<bool> { true, false }));
                    Console.WriteLine(string.Join(separator, new List<Color> { Color.Green, Color.Red }));
                    Console.WriteLine(string.Join(separator, "x", "y", "z"));
                    Console.WriteLine("[" + string.Join(separator, new string[0]) + "]");
                    Console.WriteLine(string.Join(separator, new string[] { "one" }));
                    // Control: an empty string separator
                    Console.WriteLine(string.Join("", new string[] { "a", "b" }));
                }
            }
            """, "P");
        Assert.Equal("ab\ncd\n123\n1.52\nTrueFalse\nGreenRed\nxyz\n[]\none\nab", output);
    }
}
