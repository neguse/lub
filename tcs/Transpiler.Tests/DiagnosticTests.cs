namespace TinyCs.Tests;

public class DiagnosticTests
{
    private static void AssertUnsupportedWarning(TranspileResult result, string kind)
    {
        Assert.True(result.Success);
        Assert.Contains(result.Warnings,
            w => w.Contains("unsupported") && w.Contains(kind));
    }

    [Fact]
    public void CompileError_ReportsWithLocation()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    return UndefinedVariable;
                }
            }
            """]);
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Contains("error CS", result.Errors[0]);
    }

    [Fact]
    public void ValidCode_NoErrors()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test() { return 42; }
            }
            """]);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.NotEmpty(result.Lua);
    }

    [Fact]
    public void MultipleFiles_CompileError()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "public class A { public int X; }",
            """
            public class B
            {
                public static int Test()
                {
                    var a = new A();
                    return a.Y;
                }
            }
            """
        ]);
        Assert.False(result.Success);
        Assert.Contains("error CS", result.Errors[0]);
    }

    [Fact]
    public void UnsupportedStructDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct Vec2
            {
                public int X;
            }
            """]);

        AssertUnsupportedWarning(result, "StructDeclaration");
    }

    [Fact]
    public void UnsupportedRecordStructDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public readonly record struct Vec2(int X, int Y);
            """]);

        AssertUnsupportedWarning(result, "RecordStructDeclaration");
    }

    [Fact]
    public void UnsupportedThrowStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void Test()
                {
                    throw new System.Exception();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "ThrowStatement");
    }

    [Fact]
    public void UnsupportedTryStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    try
                    {
                        return 1;
                    }
                    catch
                    {
                        return 2;
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "TryStatement");
    }

    [Fact]
    public void UnsupportedNestedSyntaxes_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    try
                    {
                        throw new System.Exception();
                    }
                    catch
                    {
                        return 2;
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "TryStatement");
        AssertUnsupportedWarning(result, "ThrowStatement");
    }

    [Fact]
    public void UnsupportedUsingStatement_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Disposable : System.IDisposable
            {
                public void Dispose() {}
            }

            public class T
            {
                public static void Test()
                {
                    using (var d = new Disposable())
                    {
                    }
                }
            }
            """]);

        AssertUnsupportedWarning(result, "UsingStatement");
    }

    [Fact]
    public void UnsupportedUsingDeclaration_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Disposable : System.IDisposable
            {
                public void Dispose() {}
            }

            public class T
            {
                public static void Test()
                {
                    using var d = new Disposable();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "UsingDeclaration");
    }

    [Fact]
    public void UnsupportedLocalFunction_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static int Test()
                {
                    int Local() => 1;
                    return Local();
                }
            }
            """]);

        AssertUnsupportedWarning(result, "LocalFunctionStatement");
    }

    [Fact]
    public void UnsupportedPattern_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static bool Test(int[] values)
                {
                    return values is [1, 2];
                }
            }
            """]);

        AssertUnsupportedWarning(result, "ListPattern");
    }

    [Fact]
    public void UnsupportedBclApi_ReportsWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.IO;

            public class T
            {
                public static string Test()
                {
                    return File.ReadAllText("save.dat");
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.Contains(result.Warnings,
            w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi)
                && w.Contains("System.IO.File.ReadAllText"));
    }

    [Fact]
    public void UnsupportedCoreLibraryMembers_ReportWarnings()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class T
            {
                public static double Test()
                {
                    var values = new List<int> { 1 };
                    values.Reverse();
                    var one = values.Single();
                    var capacity = values.Capacity;
                    var empty = string.Empty;
                    return Math.Log(one) + Math.E + capacity + empty.Length;
                }
            }
            """]);

        var apiWarnings = result.Warnings
            .Where(w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(6, apiWarnings.Length);
        Assert.Contains(apiWarnings, w => w.Contains("List<T>.Reverse"));
        Assert.Contains(apiWarnings,
            w => w.Contains("System.Linq.Enumerable.Single"));
        Assert.Contains(apiWarnings, w => w.Contains("System.Math.Log"));
        Assert.Contains(apiWarnings, w => w.Contains("List<T>.Capacity"));
        Assert.Contains(apiWarnings, w => w.Contains("string.Empty"));
        Assert.Contains(apiWarnings, w => w.Contains("System.Math.E"));
    }

    [Fact]
    public void UnsupportedCollectionNulls_ReportWarning()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System.Collections.Generic;
            using System.Linq;

            public class T
            {
                public static void Test()
                {
                    var list = new List<string> { null };
                    list.Add(default);
                    list[0] = null;

                    var dict = new Dictionary<string, string>
                    {
                        { "a", null }
                    };
                    dict["b"] = default;

                    var values = new List<string> { "a" };
                    var byValue = values.ToDictionary(v => v, v => (string)null);
                }
            }
            """]);

        var collectionNulls = result.Warnings
            .Where(w => w.Contains("unsupported collection null"))
            .ToArray();

        Assert.True(result.Success);
        Assert.Equal(6, collectionNulls.Length);
        Assert.Contains(collectionNulls, w => w.Contains("List<T>"));
        Assert.Contains(collectionNulls, w => w.Contains("Dictionary<K,V>"));
    }
}
