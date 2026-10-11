using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// Lua / 生成コードの予約と重なる識別子 (q.md Q16)。transpiler は build を止める
// error、analyzer は他の build を止める診断と同じく warning
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

    // delegate の型名も同じ規則 (DelegateDeclaration 自体も TCS1001 だが、識別子の
    // 規則が先に当たる)
    [Fact]
    public async Task DelegateWithUnderscore_Reports()
    {
        var diagnostics = await AnalyzeAsync("""
            public delegate void My_Del(int v);
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Contains("UnderscoreTypeName(My_Del)", diagnostic.GetMessage());
    }

    // host (metadata、runtime 以外の assembly) の interface の実装 (q.md Q16)。BCL の
    // interface は対象外
    [Fact]
    public async Task HostInterfaceImplementation_Reports()
    {
        var stub = CSharpCompilation.Create("HostStub",
            [CSharpSyntaxTree.ParseText("public interface IHostHook { void DoThing(int v); }")],
            [MetadataReference.CreateFromFile(typeof(object).Assembly.Location)],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        Assert.True(stub.Emit(image).Success);
        var diagnostics = await AnalyzeAsync("""
            public class Impl : IHostHook { public void DoThing(int v) { } }
            public class D : System.IDisposable { public void Dispose() { } }
            """, extraReferences: [MetadataReference.CreateFromImage(image.ToArray())]);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, diagnostic.Id);
        Assert.Contains("RefInterfaceImplementation(IHostHook)", diagnostic.GetMessage());
        Assert.Equal(0, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }
}
