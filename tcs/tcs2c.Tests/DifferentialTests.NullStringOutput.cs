namespace TinyCs.Tcs2c.Tests;

// null の string の補間と Console の出力は空文字列 (#415)。C は `nil` を書いていた
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

    // 明示した ToString() は null の受け手で fault (C# の NullReferenceException)。値を捨てる文でも、型引数が string の generic でも止まる。
    // 期待値は同じ program を CoreCLR で実行した stdout
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
