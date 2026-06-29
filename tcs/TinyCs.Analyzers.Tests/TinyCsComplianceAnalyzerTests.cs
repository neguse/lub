using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Xunit;

namespace TinyCs.Analyzers.Tests;

public class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task SupportedSubset_HasNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Player
            {
                public int X { get; set; }

                public static int Move(int x)
                {
                    if (x > 0) return x + 1;
                    return 0;
                }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task StructDeclaration_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public struct Vec2
            {
                public int X;
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, diagnostic.Id);
        Assert.Contains("StructDeclaration", diagnostic.GetMessage());
    }

    [Fact]
    public async Task RecordStructDeclaration_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public readonly record struct Vec2(int X, int Y);
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, diagnostic.Id);
        Assert.Contains("RecordStructDeclaration", diagnostic.GetMessage());
    }

    [Fact]
    public async Task UnsupportedStatements_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Demo
            {
                public static int Run()
                {
                    int Local() => 1;
                    try
                    {
                        return Local();
                    }
                    catch
                    {
                        throw;
                    }
                }
            }
            """);

        Assert.Contains(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax
            && d.GetMessage().Contains("LocalFunctionStatement"));
        Assert.Contains(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax
            && d.GetMessage().Contains("TryStatement"));
        Assert.Contains(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax
            && d.GetMessage().Contains("ThrowStatement"));
    }

    [Fact]
    public async Task ListPattern_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Demo
            {
                public static bool Run(int[] values)
                {
                    return values is [1, 2];
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, diagnostic.Id);
        Assert.Contains("ListPattern", diagnostic.GetMessage());
    }

    [Fact]
    public async Task UnsupportedBclApi_ReportsUnsupportedApi()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.IO;

            public class Demo
            {
                public static string Load(string path)
                {
                    return File.ReadAllText(path);
                }
            }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedApi, diagnostic.Id);
        Assert.Contains("System.IO.File.ReadAllText", diagnostic.GetMessage());
    }

    [Fact]
    public async Task DiagnosticSeverity_CanBeOverridden()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.IO;

            public class Demo
            {
                public static string Load(string path)
                {
                    return File.ReadAllText(path);
                }
            }
            """,
            new Dictionary<string, ReportDiagnostic>
            {
                [TinyCsDiagnosticIds.UnsupportedApi] = ReportDiagnostic.Error,
            });

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedApi, diagnostic.Id);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public async Task UnsupportedCoreLibraryMembers_ReportUnsupportedApi()
    {
        var diagnostics = await AnalyzeAsync("""
            using System;
            using System.Collections.Generic;
            using System.Linq;

            public class Demo
            {
                public static double Run()
                {
                    var values = new List<int> { 1 };
                    values.Reverse();
                    var one = values.Single();
                    var capacity = values.Capacity;
                    var empty = string.Empty;
                    return Math.Log(one) + Math.E + capacity + empty.Length;
                }
            }
            """);

        var apiDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedApi)
            .ToArray();

        Assert.Equal(6, apiDiagnostics.Length);
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("List<T>.Reverse"));
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("System.Linq.Enumerable.Single"));
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("System.Math.Log"));
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("List<T>.Capacity"));
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("string.Empty"));
        Assert.Contains(apiDiagnostics,
            d => d.GetMessage().Contains("System.Math.E"));
    }

    [Fact]
    public async Task CollectionNulls_ReportUnsupportedCollectionNull()
    {
        var diagnostics = await AnalyzeAsync("""
            using System.Collections.Generic;
            using System.Linq;

            public class Demo
            {
                public static void Run()
                {
                    var list = new List<string> { null };
                    list.Add(default);
                    list[0] = null;

                    var dict = new Dictionary<string, string>
                    {
                        { "a", null }
                    };
                    dict["b"] = default;

                    var values = new List<string> { "a" };
                    var byValue = values.ToDictionary(v => v, v => (string)null);
                }
            }
            """);

        var collectionNulls = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedCollectionNull)
            .ToArray();

        Assert.Equal(6, collectionNulls.Length);
        Assert.All(collectionNulls,
            d => Assert.Contains("null storage", d.GetMessage()));
        Assert.Contains(collectionNulls,
            d => d.GetMessage().Contains("List<T>"));
        Assert.Contains(collectionNulls,
            d => d.GetMessage().Contains("Dictionary<K,V>"));
    }

    [Fact]
    public async Task NullOutsideCollections_HasNoCollectionNullDiagnostic()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Demo
            {
                public static bool Run()
                {
                    string value = null;
                    return value == null;
                }
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.UnsupportedCollectionNull);
    }

    private static async Task<IReadOnlyList<Diagnostic>> AnalyzeAsync(
        string source,
        IDictionary<string, ReportDiagnostic>? specificDiagnosticOptions = null)
    {
        var tree = CSharpSyntaxTree.ParseText(source,
            CSharpParseOptions.Default.WithLanguageVersion(
                LanguageVersion.Preview));
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary);
        if (specificDiagnosticOptions != null)
            options = options.WithSpecificDiagnosticOptions(
                specificDiagnosticOptions);

        var compilation = CSharpCompilation.Create(
            "TinyCsAnalyzerTests",
            [tree],
            GetReferences(),
            options);

        var analyzers = ImmutableArray.Create<DiagnosticAnalyzer>(
            new TinyCsComplianceAnalyzer());
        var diagnostics = await compilation.WithAnalyzers(analyzers)
            .GetAnalyzerDiagnosticsAsync();

        return diagnostics
            .OrderBy(d => d.Location.GetLineSpan().StartLinePosition.Line)
            .ThenBy(d => d.Location.GetLineSpan().StartLinePosition.Character)
            .ToArray();
    }

    private static IReadOnlyList<MetadataReference> GetReferences()
    {
        var runtimeDir = Path.GetDirectoryName(typeof(object).Assembly.Location)!;
        var assemblyNames = new[]
        {
            "System.Runtime.dll",
            "System.Collections.dll",
            "System.Linq.dll",
            "System.Console.dll",
            "System.IO.FileSystem.dll",
        };

        return new[]
            {
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            }
            .Concat(assemblyNames
                .Select(name => Path.Combine(runtimeDir, name))
                .Where(File.Exists)
                .Select(path => MetadataReference.CreateFromFile(path)))
            .ToArray();
    }
}
