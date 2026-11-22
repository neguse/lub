namespace TinyCs.Tcs2c.Tests;

// char is a 1-byte code unit, so (char)x faults if x is outside 0 to 255 (C's message includes how to do it instead).
// CoreCLR creates a UTF-16 code unit and does not stop, so this intentionally differs here (codepoints use char.ConvertFromUtf32). .NET is not compared
public class CharRangeDifferentialTests
{
    private const string CharRangeMessage =
        "(char) cast: value is not in 0-255; use char.ConvertFromUtf32 for a codepoint";

    [CTheory]
    [InlineData("(char)Id(256)")]
    [InlineData("(char)Id(12450)")]
    [InlineData("(char)Id(-1)")]
    [InlineData("(char)Id(65601)")]
    [InlineData("(char)12450")]
    [InlineData("(char)(Id(30000) * 1.0f)")]
    public void CharCast_OutOfByteRange_FaultsWithSameMessage(string cast)
    {
        var program = $$"""
            using System;
            public static class P
            {
                public static int Id(int x) => x;
                public static void Main()
                {
                    Console.WriteLine("before");
                    char c = {{cast}};
                    Console.WriteLine("after");
                }
            }
            """;
        Assert.Equal("before", Backends.AssertBackendFaultParity(program, "P"));
        Assert.Contains(CharRangeMessage, Assert.Throws<InvalidOperationException>(() => Backends.RunC([program], "P")).Message);
    }

    [CFact]
    public void CharCast_InByteRange_KeepsValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static int Id(int x) => x;
                public static void Main()
                {
                    Console.WriteLine((int)(char)Id(0) + "," + (int)(char)Id(255) + "," + (char)Id(65) + "," + (char)65 + "," + (char)65.5f);
                    byte b = 200;
                    Console.WriteLine((int)(char)b);
                    Console.WriteLine((int)(char)'a');
                }
            }
            """, "P");
        Assert.Equal("""
            0,255,A,A,A
            200
            97
            """, output);
    }

    // A char that goes out of range without a cast (`c++`) stops at the point where it is converted to a string
    [CFact]
    public void CharIncrement_OutOfByteRange_FaultsWhenStringified()
    {
        Assert.Equal("255", Backends.AssertBackendFaultParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    char c = (char)255;
                    Console.WriteLine((int)c);
                    c++;
                    Console.WriteLine("" + c);
                }
            }
            """, "P"));
    }
}
