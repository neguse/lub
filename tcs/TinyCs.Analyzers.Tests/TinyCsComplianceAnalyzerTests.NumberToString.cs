using Xunit;

namespace TinyCs.Analyzers.Tests;

// int / float の ToString(format) は定数の標準の書式だけ (補間の書式と同じ)
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task NumberToStringFormat_ReportsUnsupportedFormatsOnly()
    {
        var diagnostics = await AnalyzeAsync("""
            public static class P
            {
                public static string Test(float f, int i, string s) =>
                    f.ToString("F2") + i.ToString("X4") + f.ToString("") + i.ToString("N0")
                    + f.ToString("0.00") + i.ToString("C") + f.ToString(s);
            }
            """);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, d => Assert.Equal(TinyCsDiagnosticIds.UnsupportedApi, d.Id));
        Assert.Equal(2, diagnostics.Count(d => d.GetMessage().Contains("float.ToString")));
        Assert.Single(diagnostics, d => d.GetMessage().Contains("int.ToString"));
    }
}
