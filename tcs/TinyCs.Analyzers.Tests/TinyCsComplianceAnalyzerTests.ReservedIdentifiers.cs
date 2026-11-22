using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// Identifiers that collide with reserved words of Lua / generated code (q.md Q16). The transpiler reports a build-stopping
// error; the analyzer reports a warning, like other build-stopping diagnostics
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task ReservedIdentifiers_ReportUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            namespace A.B_C { public class X { } }
            public class My_Type { }
            public class P
            {
                public int __hp;
                public int 速度;
                public void @new() { }
                public int ctor { get; set; }
                public static int F(int __a) { var __b = __a; return __b; }
                public int max_hp;
                public int _init;
            }
            """);

        var names = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .Select(d =>
            {
                Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
                var line = d.Location.GetLineSpan().StartLinePosition.Line + 1;
                return $"{line}:{d.GetMessage()}";
            })
            .OrderBy(s => int.Parse(s.Split(':')[0]))
            .ThenBy(s => s, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "1:TinyC# does not support syntax 'UnderscoreNamespaceName(B_C)'",
            "2:TinyC# does not support syntax 'UnderscoreTypeName(My_Type)'",
            "5:TinyC# does not support syntax 'DoubleUnderscoreIdentifier(__hp)'",
            "6:TinyC# does not support syntax 'NonAsciiIdentifier(速度)'",
            "7:TinyC# does not support syntax 'ReservedClassKey(new)'",
            "8:TinyC# does not support syntax 'ReservedClassKey(ctor)'",
            "9:TinyC# does not support syntax 'DoubleUnderscoreIdentifier(__a)'",
            "9:TinyC# does not support syntax 'DoubleUnderscoreIdentifier(__b)'",
        ], names);
    }

    [Fact]
    public async Task FileScopedNamespaceWithUnderscore_Reports()
    {
        var diagnostics = await AnalyzeAsync("""
            namespace Game_Core;
            public class X { }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("UnderscoreNamespaceName(Game_Core)", diagnostic.GetMessage());
    }

    // Delegate type names follow the same rule (DelegateDeclaration itself is TCS1001 too, but the identifier
    // rule hits first)
    [Fact]
    public async Task DelegateWithUnderscore_Reports()
    {
        var diagnostics = await AnalyzeAsync("""
            public delegate void My_Del(int v);
            """);

        // Delegate declarations are also outside TinyC# (same as the tcs compiler)
        Assert.Equal(2, diagnostics.Count);
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("UnderscoreTypeName(My_Del)"));
        Assert.Contains(diagnostics, d => d.GetMessage().Contains("DelegateDeclaration"));
    }

    // Implementations of host interfaces (`--ref` stubs; passed to the analyzer as TinyCsRef in AdditionalFiles) (q.md Q16).
    // BCL interfaces are APIs outside TinyC# (same as the tcs compiler)
    [Fact]
    public async Task HostInterfaceImplementation_Reports()
    {
        const string stub = "public interface IHostHook { void DoThing(int v); }";
        var host = CSharpCompilation.Create("HostStub", [CSharpSyntaxTree.ParseText(stub)],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(host.Emit(image).Success);
        var diagnostics = await AnalyzeAsync("""
            public class Impl : IHostHook { public void DoThing(int v) { } }
            public class D : System.IDisposable { public void Dispose() { } }
            """, extraReferences: [MetadataReference.CreateFromImage(image.ToArray())], refStubs: [stub]);

        var diagnostic = Assert.Single(diagnostics,
            d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax);
        Assert.Contains("RefInterfaceImplementation(IHostHook)", diagnostic.GetMessage());
        Assert.Equal(0, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
        var api = Assert.Single(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedApi);
        Assert.Contains("System.IDisposable", api.GetMessage());
    }
}
