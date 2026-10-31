using System.Linq;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// struct / record struct の static member と演算子はサブセットの内側。
// `==` と変換演算子は class と同じくサブセットの外
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task StructStaticsAndOperators_AreClean()
    {
        var diagnostics = await AnalyzeAsync("""
            public struct S
            {
                public int V;
                public static int X = 1;
                public const int K = 5;
                public static int Count { get; set; }
                public static int Two => 2;
                public static S Make() { return new S(); }
                public static S operator +(S a, S b) { return a; }
                public static S operator -(S a) { return a; }
            }
            public record struct RS(int a)
            {
                public static readonly int Y = 2;
                public static RS operator *(RS r, int s) { return r; }
            }
            """);

        Assert.Empty(diagnostics);
    }

    [Fact]
    public async Task ToStringOverride_IsClean_OtherOverridesReport()
    {
        var clean = await AnalyzeAsync("""
            public class C { public override string ToString() => "c"; }
            public record R(int A) { public override string ToString() => "r"; }
            public struct S { public int V; public override string ToString() => "s"; }
            public record struct RS(int A) { public override string ToString() => "rs"; }
            public static class P
            {
                public static string F(C c, S s) { return c.ToString() + c + s.ToString() + s + $"{c}{s}"; }
            }
            """);
        Assert.Empty(clean);

        var others = await AnalyzeAsync("""
            public class C { public override bool Equals(object o) => true; public override int GetHashCode() => 1; }
            public struct S { public int V; public override bool Equals(object o) => true; public override int GetHashCode() => 1; }
            """);
        Assert.Equal(4, others.Count(d => d.GetMessage().Contains("'OverrideMember'")));
    }

    [Theory]
    [InlineData("public static bool operator ==(S a, S b) => true; public static bool operator !=(S a, S b) => false;", "OperatorDeclaration(==)")]
    [InlineData("public static bool operator <(S a, S b) => true; public static bool operator >(S a, S b) => false;", "OperatorDeclaration(<)")]
    [InlineData("public static implicit operator int(S a) => a.V;", "ConversionOperatorDeclaration")]
    public async Task StructOperatorsOutsideClassRange_ReportUnsupportedSyntax(string member, string name)
    {
        var diagnostics = await AnalyzeAsync($$"""
            public struct S { public int V; {{member}} }
            """);

        Assert.Contains(diagnostics, d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax
            && d.GetMessage().Contains(name));
    }
}
