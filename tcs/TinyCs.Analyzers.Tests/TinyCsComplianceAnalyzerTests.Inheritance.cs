using Microsoft.CodeAnalysis;
using Xunit;

namespace TinyCs.Analyzers.Tests;

// class / record の継承は対象外 (q.md Q15)。transpiler は build を止める
// error、analyzer は他の build を止める診断と同じく warning
public partial class TinyCsComplianceAnalyzerTests
{
    [Fact]
    public async Task Inheritance_ReportsUnsupportedSyntax()
    {
        var diagnostics = await AnalyzeAsync("""
            public interface IShape { int Area(); }
            public abstract class B
            {
                protected int X;
                public virtual int F() => 1;
                public abstract int G();
            }
            public sealed class D : B, IShape
            {
                public D() : base() { }
                public override int F() => base.F() + 1;
                public sealed override int G() => 2;
                public new int H() => 3;
                public int Area() => 4;
            }
            public record R(int A) : IShape { public int Area() => A; }
            public record S(int A, int C) : R(A);
            """);

        var names = diagnostics
            .Where(d => d.Id == TinyCsDiagnosticIds.UnsupportedSyntax)
            .Select(d =>
            {
                Assert.Equal(DiagnosticSeverity.Warning, d.Severity);
                var line = d.Location.GetLineSpan().StartLinePosition.Line + 1;
                return $"{line}:{d.GetMessage()}";
            })
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal([
            "10:TinyC# does not support syntax 'BaseConstructorInitializer'",
            "11:TinyC# does not support syntax 'BaseMemberAccess'",
            "11:TinyC# does not support syntax 'OverrideMember'",
            "12:TinyC# does not support syntax 'OverrideMember'",
            "13:TinyC# does not support syntax 'NewMemberHiding'",
            "17:TinyC# does not support syntax 'ClassInheritance(R)'",
            "2:TinyC# does not support syntax 'AbstractClass'",
            "4:TinyC# does not support syntax 'ProtectedMember'",
            "5:TinyC# does not support syntax 'VirtualMember'",
            "6:TinyC# does not support syntax 'AbstractMember'",
            "8:TinyC# does not support syntax 'ClassInheritance(B)'",
        ], names);
    }
}
