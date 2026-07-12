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
    public async Task PartialTypesAndLock_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public partial class PartialClass
            {
                public static int First() => 1;
            }
            public partial class PartialClass
            {
                public static int Second() => 2;
            }
            public partial record PartialRecord;
            public partial interface IPartial { }

            public class RegularClass
            {
                public static void Run()
                {
                    lock (new object())
                    {
                    }
                }
            }

            public record RegularRecord;
            public interface IRegular { }
            """);

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        Assert.Equal(5, syntaxDiagnostics.Length);
        Assert.Equal(4, syntaxDiagnostics.Count(
            d => d.GetMessage().Contains("PartialTypeDeclaration")));
        Assert.Single(syntaxDiagnostics,
            d => d.GetMessage().Contains("LockStatement"));
    }

    [Fact]
    public async Task PartialStructs_KeepExistingSingleDiagnostic()
    {
        var diagnostics = await AnalyzeAsync("""
            public partial struct PartialStruct { }
            public readonly partial record struct PartialRecordStruct(int Value);
            """);

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        Assert.Equal(2, syntaxDiagnostics.Length);
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("StructDeclaration"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("RecordStructDeclaration"));
        Assert.DoesNotContain(syntaxDiagnostics,
            d => d.GetMessage().Contains("PartialTypeDeclaration"));
    }

    [Fact]
    public async Task NameOfExpressions_ReportUnsupportedSyntax()
    {
        const string source = """
            public class NameDemo
            {
                public static string Simple(int value) => nameof(value);
                public static string MemberName() => nameof(System.Math.E);
                public static string TypeName() => nameof(System.DateTime);
            }
            """;
        var diagnostics = await AnalyzeAsync(source);

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        Assert.Equal(3, syntaxDiagnostics.Length);
        Assert.Equal(3, diagnostics.Count);
        Assert.All(syntaxDiagnostics,
            d => Assert.Contains("NameOfExpression", d.GetMessage()));
        Assert.Equal([
            "nameof(value)",
            "nameof(System.Math.E)",
            "nameof(System.DateTime)",
        ], syntaxDiagnostics.Select(d => source.Substring(
            d.Location.SourceSpan.Start, d.Location.SourceSpan.Length)));
    }

    [Fact]
    public async Task UserMethodNamedNameof_IsNotNameOfExpression()
    {
        var diagnostics = await AnalyzeAsync("""
            public class NameDemo
            {
                public static string nameof(string value) => value;
                public static string Run() => nameof("ok");
            }
            """);

        Assert.DoesNotContain(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax);
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
    public async Task FullyQualifiedApis_ReportOnlyUnsupportedMember()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Demo
            {
                public static int Supported() => System.Math.Min(3, 7);

                public static string Unsupported() =>
                    System.IO.File.ReadAllText("save.dat");
            }
            """);

        var diagnostic = Assert.Single(diagnostics,
            value => value.Id == TinyCsDiagnosticIds.UnsupportedApi);
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
                    return Math.Cbrt(one) + Math.E + capacity + empty.Length;
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
            d => d.GetMessage().Contains("System.Math.Cbrt"));
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

    [Fact]
    public async Task SupportedOperatorOverloads_HaveNoDiagnostics()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Vec2
            {
                public double X;
                public double Y;

                public Vec2(double x, double y)
                {
                    X = x;
                    Y = y;
                }

                public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
                public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
                public static Vec2 operator -(Vec2 a) => new Vec2(-a.X, -a.Y);
                public static Vec2 operator *(Vec2 a, double s) => new Vec2(a.X * s, a.Y * s);
                public static Vec2 operator *(double s, Vec2 a) => new Vec2(s * a.X, s * a.Y);
                public static Vec2 operator /(Vec2 a, double s) => new Vec2(a.X / s, a.Y / s);
                public static Vec2 operator %(Vec2 a, Vec2 b) => new Vec2(a.X % b.X, a.Y % b.Y);
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task UnsupportedOperatorOverloads_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Id
            {
                public int Value;

                public static bool operator ==(Id a, Id b) => a.Value == b.Value;
                public static bool operator !=(Id a, Id b) => a.Value != b.Value;
                public static implicit operator int(Id a) => a.Value;
                public override bool Equals(object o) => false;
                public override int GetHashCode() => 0;
            }
            """);

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        Assert.Equal(3, syntaxDiagnostics.Length);
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("OperatorDeclaration(==)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("OperatorDeclaration(!=)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("ConversionOperatorDeclaration"));
    }

    [Fact]
    public async Task OutRefParameters_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public class Parser
            {
                public static bool TryParse(string text, out int value)
                {
                    value = 0;
                    return false;
                }

                public static void Bump(ref int value)
                {
                    value = value + 1;
                }
            }
            """);

        var syntaxDiagnostics = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .ToArray();

        Assert.Equal(2, syntaxDiagnostics.Length);
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("OutParameter"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("RefParameter"));
    }

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

        Assert.Equal(4, syntaxDiagnostics.Length);
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(until)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(end)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(repeat)"));
        Assert.Contains(syntaxDiagnostics,
            d => d.GetMessage().Contains("LuaKeywordIdentifier(nil)"));
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
