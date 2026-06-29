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
}
