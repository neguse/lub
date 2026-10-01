using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// Lua 識別子 (予約語 / verbatim / ローカル束縛) の診断。本体は
// TinyCsComplianceAnalyzerTests.cs (800 行の上限で分割)。
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task LuaKeywordIdentifiers_ReportUnsupportedSyntax()
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

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        // member (field / method) だけ診断する。ローカル束縛 (repeat / @nil)
        // は transpiler が安全な名前に写すので対象外
        Assert.Equal(2, syntaxDiagnostics.Length);
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(until)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(end)"));
        Assert.DoesNotContain(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(repeat)"));
        Assert.DoesNotContain(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(nil)"));
    }

    [Fact]
    public async Task LuaKeywordLocalBindings_HaveNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;

            public class Turn
            {
                public int Run(int end, List<int> items)
                {
                    var local = end;
                    foreach (var until in items) local += until;
                    object boxed = local;
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
