namespace TinyCs.Tcs2c.Tests;

// Type-argument values holding an enum, and string.Join over a List / array of enums, are converted to member names
// (Lua erases types, so the way to look up names is passed together with the type argument's default value).
// The expected values are the stdout of running the same program on CoreCLR
public class EnumTypeArgToStringDifferentialTests
{
    [CFact]
    public void EnumTypeArgumentAndJoin_UseMemberNames()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum Color { Red, Green, Blue }
            public static class G
            {
                public static string Show<T>(T x) => "" + x + "|" + $"{x}";
                public static string Twice<T>(T x) => Show(x) + "/" + Show(x);
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.Show(Color.Green) + "|" + G.Twice(Color.Blue) + "|" + G.Show(3));
                    Console.WriteLine(string.Join(",", new List<Color> { Color.Red, Color.Blue }));
                    Console.WriteLine(string.Join("-", new[] { Color.Green, Color.Red }));
                }
            }
            """, "P");
        Assert.Equal("""
            Green|Green|Blue|Blue/Blue|Blue|3|3
            Red,Blue
            Green-Red
            """, output);
    }
}
