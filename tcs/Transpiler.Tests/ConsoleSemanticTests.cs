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
}
