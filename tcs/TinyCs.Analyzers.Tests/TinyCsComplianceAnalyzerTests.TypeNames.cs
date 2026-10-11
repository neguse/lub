using Xunit;

namespace TinyCs.Analyzers.Tests;

// 型の Lua global 名の衝突 (`A_B.C` と `A.B.C` は共に `A_B_C`)。
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task LuaTypeNameCollision_ReportsSecondTypeOnly()
    {
        var diagnostics = await AnalyzeAsync("""
            namespace A_B { public class C { } }
            namespace A { namespace B { public class C { } } }
            namespace A.B { public interface IShape { } }
            namespace A_B { public interface IShape { } }
            """);

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(TinyCsDiagnosticIds.UnsupportedSyntax, diagnostic.Id);
        Assert.Contains("LuaNameCollision(A_B.C/A.B.C)", diagnostic.GetMessage());
        Assert.Equal(1, diagnostic.Location.GetLineSpan().StartLinePosition.Line);
    }
}
