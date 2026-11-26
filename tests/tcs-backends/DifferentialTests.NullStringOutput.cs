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

    // An explicit ToString() faults on a null receiver (C#'s NullReferenceException). It stops in a statement that discards the value too, and in a generic whose type argument is string.
    // The expected value is the stdout of the same program run on CoreCLR
    [CTheory]
    [InlineData("Console.WriteLine(value.ToString());")]
    [InlineData("value.ToString();")]
    [InlineData("Console.WriteLine(\"[\" + value.ToString() + \"]\");")]
    [InlineData("Console.WriteLine($\"[{value.ToString()}]\");")]
    [InlineData("Console.WriteLine(Text(value));")]
    [InlineData("Console.WriteLine(Nil().ToString());")]
    public void NullString_ExplicitToString_FaultsAfterPrecedingOutput(string body)
    {
        Assert.Equal("before", Backends.AssertFaultParity($$"""
            using System;
            public static class P
            {
                static string Nil() => null;
                static string Text<T>(T v) => v.ToString();
                public static void Main()
                {
                    string value = null;
                    Console.WriteLine("before");
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    [CFact]
    public void String_ExplicitToString_ReturnsSameText()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                static string Text<T>(T v) => v.ToString();
                public static void Main()
                {
                    string value = "hello";
                    string none = null;
                    Console.WriteLine(value.ToString() + "," + Text(value) + "," + Text(3));
                    Console.WriteLine("[" + none?.ToString() + "]");
                    value.ToString();
                }
            }
            """, "P");
        Assert.Equal("hello,hello,3\n[]", output);
    }
}
