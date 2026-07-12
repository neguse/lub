namespace TinyCs.Tests;

// T167: Math.Round / Sign / Tan / Log / Exp と String.IsNullOrEmpty の
// runtime + facade + allowlist 3点セットを固定する。
public class BclAllowlistExtensionTests
{
    [Fact]
    public void Math_Round_UsesBankersRounding()
    {
        // C# Math.Round は MidpointRounding.ToEven (偶数丸め)
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Round(2.5) + ":" + Math.Round(3.5) + ":" +
                    Math.Round(2.4) + ":" + Math.Round(-2.5) + ":" + Math.Round(-2.6);
            }
            """, "T.Test()");
        Assert.Equal("2:4:2:-2:-3", result);
    }

    [Fact]
    public void Math_Round_WithDigits()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static double Test() => Math.Round(3.14159, 2);
            }
            """, "T.Test()");
        Assert.Equal("3.14", result);
    }

    [Fact]
    public void Math_Sign()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static string Test() =>
                    Math.Sign(-12.5) + ":" + Math.Sign(0) + ":" + Math.Sign(3);
            }
            """, "T.Test()");
        Assert.Equal("-1:0:1", result);
    }

    [Fact]
    public void Math_Tan()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static bool Test() => Math.Abs(Math.Tan(Math.PI / 4) - 1.0) < 1e-9;
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Math_Log_NaturalAndBase()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static bool Test() =>
                    Math.Abs(Math.Log(Math.Exp(1.0)) - 1.0) < 1e-9
                    && Math.Abs(Math.Log(8.0, 2.0) - 3.0) < 1e-9;
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void Math_Exp()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            public class T
            {
                public static bool Test() => Math.Abs(Math.Exp(1.0) - 2.718281828) < 1e-6;
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }

    [Fact]
    public void String_IsNullOrEmpty()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static string Test()
                {
                    string? missing = null;
                    return $"{string.IsNullOrEmpty(missing)}:{string.IsNullOrEmpty("")}:{string.IsNullOrEmpty("x")}";
                }
            }
            """, "T.Test()");
        Assert.Equal("true:true:false", result);
    }

    [Fact]
    public void NewMembers_PassCheckWithoutTcs1002()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            public class T
            {
                public static bool Test(string s) =>
                    !string.IsNullOrEmpty(s)
                    && Math.Round(Math.Log(Math.Exp(Math.Tan(0.5)))) >= Math.Sign(-1.0);
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain(result.Warnings,
            w => w.Contains(TinyCsDiagnosticIds.UnsupportedApi));
    }

    [Fact]
    public void StillUnsupportedMathMembers_ReportTcs1002()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            public class T
            {
                public static double Test() => Math.Log10(100.0);
            }
            """]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains(result.Warnings, w =>
            w.Contains(TinyCsDiagnosticIds.UnsupportedApi)
            && w.Contains("System.Math.Log10"));
    }

    [Fact]
    public void TinySystemFacade_NewMathAndStringMembers()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class T
            {
                public static bool Test() =>
                    TinySystem.Math.Round(2.5) == 2.0
                    && TinySystem.Math.Sign(-3.0) == -1
                    && TinySystem.Math.Tan(0.0) == 0.0
                    && TinySystem.Math.Log(1.0) == 0.0
                    && TinySystem.Math.Exp(0.0) == 1.0
                    && TinySystem.String.IsNullOrEmpty("")
                    && !TinySystem.String.IsNullOrEmpty("x");
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }
}
