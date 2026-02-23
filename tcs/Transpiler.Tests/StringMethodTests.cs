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
            "T.Test()");
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
            "tostring(T.Test())");
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
            "T.Test()");
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
            "tostring(T.Test())");
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
            "tostring(T.Test())");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
        Assert.Equal("hello", result);
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
            "T.Test()");
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
            "T.Test()");
        Assert.Equal("42", result);
    }
}
