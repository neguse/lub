using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// Diagnostics for identifiers (same names as reserved words of other languages / verbatim / local bindings). The main tests are in
// TinyCsComplianceAnalyzerTests.cs.
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task IdentifiersNamedLikeOtherLanguageKeywords_HaveNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Turn
            {
                public int until;

                public void end()
                {
                    var repeat = 3;
                    Use(repeat);
                }

                public void Wait(int @nil) => Use(@nil);

                private static void Use(int value) { }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax);
    }

    [Fact]
    public async Task LocalBindingsNamedLikeOtherLanguageKeywords_HaveNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;

            public class Turn
            {
                public int Run(int end, List<int> items)
                {
                    var local = end;
                    foreach (var until in items) local += until;
                    int? boxed = local;
                    if (boxed is int nil) local += nil;
                    return local;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax);
    }

    [Fact]
    public async Task NonKeywordVerbatimIdentifiers_HaveNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Calc
            {
                public int @float;

                public int Add(int @out)
                {
                    var @value = @out + @float;
                    return @value;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }
}
