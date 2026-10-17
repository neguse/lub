namespace TinyCs.Tests;

public class ConsoleSemanticTests
{
    [Fact]
    public void Console_WriteLine_String()
    {
        var lua = Transpiler.Transpile("""
            using System;
            public class T
            {
                public static void Test()
                {
                    Console.WriteLine("hello");
                }
            }
            """);
        var script = $"{lua}\nT.Test()";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Console_WriteLine_Int()
    {
        var lua = Transpiler.Transpile("""
            using System;
            public class T
            {
                public static void Test()
                {
                    Console.WriteLine(42);
                }
            }
            """);
        var script = $"{lua}\nT.Test()";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("42", result);
    }

    // object の値は float を .NET と同じ文字列に、null を空行にする
    [Fact]
    public void Console_WriteLine_Object()
    {
        var lua = Transpiler.Transpile("""
            using System;
            public class T
            {
                public static void Test()
                {
                    object f = 1000000f;
                    object? n = null;
                    object i = 7;
                    Console.WriteLine(f);
                    Console.WriteLine(n);
                    Console.WriteLine(i);
                }
            }
            """);
        var script = $"{lua}\nT.Test()";
        var result = TestHelper.RunLua(script).Trim();
        Assert.Equal("1000000\n\n7", result.ReplaceLineEndings("\n"));
    }
}
