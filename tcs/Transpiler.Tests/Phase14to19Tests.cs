namespace TinyCs.Tests;

public class Phase14to19Tests
{
    // ===== T85: Property Pattern =====

    [Fact]
    public void PropertyPattern_SingleProperty()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static bool Test()
                {
                    var p = new Point(5, 3);
                    return p is { X: > 0 };
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void PropertyPattern_MultipleProperties()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static bool Test()
                {
                    var p = new Point(5, -3);
                    return p is { X: > 0, Y: > 0 };
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("false", result);
    }

    [Fact]
    public void PropertyPattern_InSwitchExpression()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static string Test()
                {
                    var p = new Point(5, 3);
                    return p switch
                    {
                        { X: > 0, Y: > 0 } => "Q1",
                        { X: < 0, Y: > 0 } => "Q2",
                        _ => "other"
                    };
                }
            }
            """, "T.Test()");
        Assert.Equal("Q1", result);
    }

    // ===== T86: with expression =====

    [Fact]
    public void With_SingleField()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p1 = new Point(1, 2);
                    var p2 = p1 with { X = 10 };
                    return p2.X + p2.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("12", result);
    }

    [Fact]
    public void With_OriginalUnchanged()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p1 = new Point(1, 2);
                    var p2 = p1 with { X = 10 };
                    return p1.X;
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void With_MultipleFields()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p1 = new Point(1, 2);
                    var p2 = p1 with { X = 10, Y = 20 };
                    return p2.X + p2.Y;
                }
            }
            """, "T.Test()");
        Assert.Equal("30", result);
    }

    // ===== T87: Deconstruct =====

    [Fact]
    public void Deconstruct_Record()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static int Test()
                {
                    var p = new Point(3, 4);
                    var (x, y) = p;
                    return x + y;
                }
            }
            """, "T.Test()");
        Assert.Equal("7", result);
    }

    // ===== T88: record value-based Equals =====

    [Fact]
    public void RecordEquals_SameValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static bool Test()
                {
                    var p1 = new Point(1, 2);
                    var p2 = new Point(1, 2);
                    return p1 == p2;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void RecordEquals_DifferentValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public record Point(int X, int Y);
            public class T
            {
                public static bool Test()
                {
                    var p1 = new Point(1, 2);
                    var p2 = new Point(3, 4);
                    return p1 == p2;
                }
            }
            """, "tostring(T.Test())");
        Assert.Equal("false", result);
    }

    // ===== T89: Extension methods =====

    [Fact]
    public void ExtensionMethod_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class StringExt
            {
                public static string Shout(this string s)
                {
                    return s + "!";
                }
            }
            public class T
            {
                public static string Test()
                {
                    return "hello".Shout();
                }
            }
            """, "T.Test()");
        Assert.Equal("hello!", result);
    }

    [Fact]
    public void ExtensionMethod_WithArgs()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class IntExt
            {
                public static int Add(this int x, int y)
                {
                    return x + y;
                }
            }
            public class T
            {
                public static int Test()
                {
                    int a = 10;
                    return a.Add(5);
                }
            }
            """, "T.Test()");
        Assert.Equal("15", result);
    }

    // ===== T90: Collection initializer (indexer) =====

    [Fact]
    public void Dict_IndexerInitializer()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var dict = new Dictionary<string, int>
                    {
                        ["a"] = 1,
                        ["b"] = 2
                    };
                    return dict["a"] + dict["b"];
                }
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }

    // ===== T91: String interpolation format specifiers =====

    [Fact]
    public void StringInterpolation_FormatF2()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    double pi = 3.14159;
                    return $"{pi:F2}";
                }
            }
            """, "T.Test()");
        Assert.Equal("3.14", result);
    }

    [Fact]
    public void StringInterpolation_FormatD3()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int n = 7;
                    return $"{n:D3}";
                }
            }
            """, "T.Test()");
        Assert.Equal("007", result);
    }

    // ===== T92: IIFE optimization (ternary safe shorthand) =====

    [Fact]
    public void Ternary_StillWorks()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int x = 5;
                    int y = x > 3 ? 10 : 20;
                    return y;
                }
            }
            """, "T.Test()");
        Assert.Equal("10", result);
    }

    // ===== T95: SourceMap =====

    [Fact]
    public void SourceMap_HasMappings()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class T
            {
                public static int Test()
                {
                    return 42;
                }
            }
            """],
            ["test.cs"]);
        Assert.True(result.Success);
        Assert.True(result.SourceMap!.Count > 0);
        var json = result.SourceMap.ToJson();
        Assert.Contains("test.cs", json);
    }

    [Fact]
    public void SourceMap_LookupReturnsCorrectFile()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class T
            {
                public static int Foo() { return 1; }
            }
            """],
            ["myfile.cs"]);
        Assert.True(result.Success);
        // The source map should reference myfile.cs
        var json = result.SourceMap!.ToJson();
        Assert.Contains("myfile.cs", json);
    }

    [Fact]
    public void SourceMap_ReverseLookup()
    {
        var result = Transpiler.TranspileWithDiagnostics(
            ["""
            public class Calc
            {
                public static int Add(int a, int b)
                {
                    return a + b;
                }
            }
            """],
            ["calc.cs"]);
        Assert.True(result.Success);
        // Find the Lua line that maps to our source
        var found = false;
        for (int i = 1; i <= 20; i++)
        {
            var entry = result.SourceMap!.Lookup(i);
            if (entry?.File == "calc.cs")
            {
                found = true;
                break;
            }
        }
        Assert.True(found);
    }

    private static string TranspileAndRunWithRuntime(string csharpSource, string luaExpr) =>
        TestHelper.TranspileAndRunWithRuntime(csharpSource, luaExpr);
}
