namespace TinyCs.Tests;

/// <summary>
/// Tests for string method transpilation.
/// Requires runtime/tinysystem.lua for String helper functions.
/// </summary>
public class StringMethodTests
{
    private static string TranspileAndRunWithRuntime(string csharpSource, string luaExpr)
    {
        var lua = Transpiler.Transpile(csharpSource);
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
                     "String = TinySystem.String\n" +
                     $"{lua}\nprint({luaExpr})";
        return TestHelper.RunLua(script).Trim();
    }

    [Fact]
    public void String_Length()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string s = "hello";
                    return s.Length;
                }
            }
            """,
            "T.test()");
        Assert.Equal("5", result);
    }

    [Fact]
    public void String_Contains()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello world";
                    return s.Contains("world");
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void String_Replace()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "hello world";
                    return s.Replace("world", "lua");
                }
            }
            """,
            "T.test()");
        Assert.Equal("hello lua", result);
    }

    [Fact]
    public void String_StartsWith()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello world";
                    return s.StartsWith("hello");
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void String_EndsWith()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test()
                {
                    string s = "hello world";
                    return s.EndsWith("world");
                }
            }
            """,
            "tostring(T.test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void String_Trim()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "  hello  ";
                    return s.Trim();
                }
            }
            """,
            "T.test()");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void String_Substring()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "hello world";
                    return s.Substring(6);
                }
            }
            """,
            "T.test()");
        Assert.Equal("world", result);
    }

    [Fact]
    public void String_SubstringWithLength()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "hello world";
                    return s.Substring(0, 5);
                }
            }
            """,
            "T.test()");
        Assert.Equal("hello", result);
    }

    [Fact]
    public void String_IndexOf()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string s = "hello world";
                    return s.IndexOf("world");
                }
            }
            """,
            "T.test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void String_IndexOf_NotFound()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string s = "hello";
                    return s.IndexOf("x");
                }
            }
            """,
            "T.test()");
        Assert.Equal("-1", result);
    }

    [Fact]
    public void String_IndexOf_StartIndex()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static int Test()
                {
                    string s = "hello hello";
                    return s.IndexOf("hello", 1);
                }
            }
            """,
            "T.test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void String_Join_List()
    {
        var result = TranspileAndRunWithRuntime("""
            using System.Collections.Generic;

            public class T
            {
                public static string Test()
                {
                    var parts = new List<string> { "red", "blue", "green" };
                    return string.Join("/", parts);
                }
            }
            """,
            "T.test()");
        Assert.Equal("red/blue/green", result);
    }

    [Fact]
    public void String_Join_Params()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    return string.Join("/", "red", "blue", "green");
                }
            }
            """,
            "T.test()");
        Assert.Equal("red/blue/green", result);
    }

    [Fact]
    public void String_ToUpperLower()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string s = "Hello";
                    return s.ToUpper() + s.ToLower();
                }
            }
            """,
            "T.test()");
        Assert.Equal("HELLOhello", result);
    }

    [Fact]
    public void Int_ToString()
    {
        var result = TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    int x = 42;
                    return x.ToString();
                }
            }
            """,
            "T.test()");
        Assert.Equal("42", result);
    }
}
