using Microsoft.CodeAnalysis;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// 値で比べる key の Dictionary (#185)。transpiler は build を止める error、
// analyzer は warning のまま (.editorconfig で severity を上げられる)
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task DictValueEqualityKey_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Linq;
            public record struct K(int x);
            public record R(int x);
            public class C { }
            public static class P
            {
                public static int Test()
                {
                    var d = new Dictionary<K, int>();
                    Dictionary<R, int> r = new();
                    var t = new List<int> { 1 }.ToDictionary(x => new K(x));
                    var c = new Dictionary<C, int>();
                    var i = new Dictionary<int, K>();
                    return d.Count + r.Count + t.Count + c.Count + i.Count;
                }
            }
            """);

        Assert.Equal(3, diagnostics.Count);
        Assert.All(diagnostics, d =>
        {
            Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, d.Id);
            Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
        });
        Assert.Contains("ValueEqualityDictionaryKey(K)", diagnostics[0].GetMessage());
        Assert.Contains("ValueEqualityDictionaryKey(R)", diagnostics[1].GetMessage());
        Assert.Contains("ValueEqualityDictionaryKey(K)", diagnostics[2].GetMessage());
    }
}
