using Xunit;

namespace TinyCs.Analyzers.Tests;

// System.Math transcendental functions (TCS1004). On .NET the .NET implementation runs, so
// code is made to go through TinySystem.Math (TinySystem.props points Math to it). MathF only gets TCS1002
public partial class TinyCsComplianceAnalyzerTests
{
    [Theory]
    [InlineData("System.Math.Sin(x)", "System.Math.Sin")]
    [InlineData("(float)Math.Atan2(x, 1f)", "System.Math.Atan2")]
    public async Task DotnetMath_TranscendentalReportsTcs1004(string expression, string apiName)
    {
        var diagnostics = await AnalyzeAsync($$"""
            using System;
            public class Demo
            {
                public static float F(float x) => (float){{expression}};
            }
            """);

        var diagnostic = Assert.Single(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.DotnetMath);
        Assert.Contains(apiName, diagnostic.GetMessage());
    }

    [Fact]
    public async Task MathF_ReportsOnlyTcs1002()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            public class Demo
            {
                public static float F(float x) => MathF.Pow(x, 2f);
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == TinyCsDiagnosticIds.DotnetMath);
        Assert.Contains(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedApi
            && d.GetMessage().Contains("System.MathF.Pow"));
    }

    [Fact]
    public async Task DotnetMath_UsingStaticReportsTcs1004()
    {
        var diagnostics = await AnalyzeAsync("""
            using static System.Math;
            public class Demo
            {
                public static float F(float x) => (float)Cos(x);
            }
            """);

        Assert.Single(diagnostics, d => d.Id == TinyCsDiagnosticIds.DotnetMath);
    }

    [Fact]
    public async Task DotnetMath_IeeeExactMembersAndTinySystemAliasPass()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using Math = TinySystem.Math;
            namespace TinySystem
            {
                public static class Math
                {
                    public static float Sin(float x) => x;
                }
            }
            public class Demo
            {
                public static float F(float x) =>
                    Math.Sin(x) + (float)System.Math.Sqrt(x) + System.Math.Abs(x)
                    + (float)System.Math.Floor(x) + System.Math.Max(x, 1f);
            }
            """);

        Assert.DoesNotContain(diagnostics, d => d.Id == TinyCsDiagnosticIds.DotnetMath);
    }
}
