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
                    Math.Round(2.5f) + ":" + Math.Round(3.5f) + ":" +
                    Math.Round(2.4f) + ":" + Math.Round(-2.5f) + ":" + Math.Round(-2.6f);
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
                public static float Test() => (float)Math.Round(3.14159f, 2);
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
                    Math.Sign(-12.5f) + ":" + Math.Sign(0) + ":" + Math.Sign(3);
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
                public static bool Test() => Math.Abs(Math.Tan(Math.PI / 4) - 1.0f) < 1e-9f;
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
                    Math.Abs(Math.Log(Math.Exp(1.0f)) - 1.0f) < 1e-6f
                    && Math.Abs(Math.Log(8.0f, 2.0f) - 3.0f) < 1e-6f; // f32 精度 (M4)
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
                public static bool Test() => Math.Abs(Math.Exp(1.0f) - 2.718281828f) < 1e-6f;
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
                    && Math.Round(Math.Log(Math.Exp(Math.Tan(0.5f)))) >= Math.Sign(-1.0f);
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
                public static float Test() => (float)Math.Log10(100.0f);
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
                    TinySystem.Math.Round(2.5f) == 2.0f
                    && TinySystem.Math.Sign(-3.0f) == -1
                    && TinySystem.Math.Tan(0.0f) == 0.0f
                    && TinySystem.Math.Log(1.0f) == 0.0f
                    && TinySystem.Math.Exp(0.0f) == 1.0f
                    && TinySystem.String.IsNullOrEmpty("")
                    && !TinySystem.String.IsNullOrEmpty("x");
            }
            """, "tostring(T.Test())");
        Assert.Equal("true", result);
    }
}
