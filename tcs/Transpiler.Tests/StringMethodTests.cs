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

    [Theory]
    [InlineData("''", "")]
    [InlineData("' \\t\\r\\n\\v\\f'", "")]
    [InlineData("'a'", "a")]
    [InlineData("' \\ta b\\n '", "a b")]
    [InlineData("'a\\nb'", "a\\nb")]
    public void String_Trim_Runtime(string luaString, string expected)
    {
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var output = TestHelper.RunLua($"""
            local TinySystem = dofile("{runtimePath}")
            io.write((string.gsub(TinySystem.String.Trim({luaString}), "\n", "\\n")))
            """);
        Assert.Equal(expected, output);
    }

    // 空白の連続の長さに対して線形。2 乗の時間なら RunLua の timeout に当たる
    [Fact]
    public void String_Trim_LongInnerWhitespaceIsLinear()
    {
        var runtimePath = TestHelper.FindProjectFile("runtime/tinysystem.lua");
        var output = TestHelper.RunLua($"""
            local TinySystem = dofile("{runtimePath}")
            local trimmed = TinySystem.String.Trim("x" .. (" "):rep(1000000) .. "y")
            print(#trimmed)
            """);
        Assert.Equal("1000002", output.Trim());
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
