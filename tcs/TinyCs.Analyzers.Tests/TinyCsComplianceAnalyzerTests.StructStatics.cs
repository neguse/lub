using Microsoft.CodeAnalysis;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// struct / record struct の static field (#154)。transpiler は build を止める
// error、analyzer は warning のまま (.editorconfig で severity を上げられる)
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task StructStaticField_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public struct S { public int V; public static int X = 1; public const int K = 5; }
            public record struct RS(int a) { public static readonly int Y = 2; }
            public struct T { public int V; public static int Count { get; set; } }
            """);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, d.Id);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
            Assert.Contains("StructStaticField", d.GetMessage());
        });
    }
}
