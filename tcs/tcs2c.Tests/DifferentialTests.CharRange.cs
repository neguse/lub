namespace TinyCs.Tcs2c.Tests;

// char は 1 byte の code unit なので、(char)x の x が 0〜255 の外なら Lua / C とも同じ文言で fault する。
// CoreCLR は UTF-16 の code unit を作るので、ここだけ意図して違う (codepoint は char.ConvertFromUtf32)
public partial class DifferentialTests
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
        Assert.Equal("before", Backends.AssertFaultParity(program, "P"));
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.RunC([program], "P"),
                 })
            Assert.Contains(CharRangeMessage, Assert.Throws<InvalidOperationException>(run).Message);
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

    // cast を通らずに範囲を出た char (`c++`) は文字列化の地点で止まる
    [CFact]
    public void CharIncrement_OutOfByteRange_FaultsWhenStringified()
    {
        Assert.Equal("255", Backends.AssertFaultParity("""
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
