using Microsoft.CodeAnalysis;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// object に入った数と bool の pattern (BoxedValuePattern) と float の key の Dictionary。transpiler は build を止める error、analyzer は warning
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task BoxedValuePatternAndFloatKey_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            public static class P
            {
                public static int Test(object o, int? m)
                {
                    var r = 0;
                    if (o is int x) r += x;
                    if (o is 1.5f) r++;
                    if (o is bool) r += (int)o;
                    if (m is int y) r += y;
                    var d = new Dictionary<float, int>();
                    var h = new HashSet<float>();
                    return r + d.Count + h.Count;
                }
            }
            """);

        Assert.Equal(4, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, d.Id);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });
        Assert.Contains("BoxedValuePattern(int)", diagnostics[0].GetMessage());
        Assert.Contains("BoxedValuePattern(float)", diagnostics[1].GetMessage());
        Assert.Contains("UnsupportedDictionaryKey(float)", diagnostics[2].GetMessage());
        Assert.Contains("UnsupportedHashSetElement(float)", diagnostics[3].GetMessage());
    }
}
