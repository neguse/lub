namespace TinyCs.Tests;

public class ConsoleSemanticTests
{
    [Fact]
    public void Console_WriteLine_String()
    {
        var result = TestHelper.CompileAndRunVmMain("""
            using System;
            public class T
            {
                public static void Test()
                {
                    Console.WriteLine("hello");
                }
            }
            """, "T.Test();");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void Console_WriteLine_Int()
    {
        var result = TestHelper.CompileAndRunVmMain("""
            using System;
            public class T
            {
                public static void Test()
                {
                    Console.WriteLine(42);
                }
            }
            """, "T.Test();");
        Assert.Equal("42", result);
    }

    // object values print float as the same string as .NET, and null as an empty line
    [Fact]
    public void Console_WriteLine_Object()
    {
        var result = TestHelper.CompileAndRunVmMain("""
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
            """, "T.Test();");
        Assert.Equal("1000000\n\n7", result.ReplaceLineEndings("\n"));
    }
}
