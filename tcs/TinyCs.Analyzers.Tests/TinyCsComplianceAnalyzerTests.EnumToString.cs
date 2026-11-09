using Xunit;

namespace TinyCs.Analyzers.Tests;

// Enum.ToString の書式は "G" / "F" / "D" の定数だけ (#133)
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task EnumToStringFormat_ReportsUnsupportedFormatsOnly()
    {
        var diagnostics = await AnalyzeAsync("""
            public enum Color { Red, Green }
            public static class P
            {
                public static string Test(string f) =>
                    Color.Green.ToString() + Color.Green.ToString("F")
                    + Color.Green.ToString("x") + Color.Green.ToString(f);
            }
            """);

        Assert.Equal(2, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(TinyCsDiagnosticIds.UnsupportedApi, d.Id);
            Assert.Contains("System.Enum.ToString", d.GetMessage());
        });
    }
}
