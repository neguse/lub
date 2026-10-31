namespace TinyCs.Tests;

// class / record の継承は TinyC# の対象外 (q.md Q15)。基底 class の宣言と、
// 継承があって初めて意味を持つ member は build を止める TCS1001 で、transpiler
// は Lua を出さない error、check は exit 1。interface の実装はそのまま書ける
[Collection(ConsoleCollection.Name)]
public class InheritanceDiagnosticTests
{
    private const string Source = """
        public abstract class B
        {
            protected int X;
            public virtual int F() => 1;
            public abstract int G();
        }
        public sealed class D : B
        {
            public D() : base() { }
            public override int F() => base.F() + 1;
            public sealed override int G() => 2;
            public new int H() => 3;
        }
        public record R(int A);
        public record S(int A, int C) : R(A);
        public class E
        {
            public override int GetHashCode() => 1;
        }
        """;

    private static readonly string[] Expected =
    [
        "(1,1): error TCS1001: unsupported syntax: AbstractClass",
        "(3,5): error TCS1001: unsupported syntax: ProtectedMember",
        "(4,5): error TCS1001: unsupported syntax: VirtualMember",
        "(5,5): error TCS1001: unsupported syntax: AbstractMember",
        "(7,25): error TCS1001: unsupported syntax: ClassInheritance(B)",
        "(9,16): error TCS1001: unsupported syntax: BaseConstructorInitializer",
        "(10,5): error TCS1001: unsupported syntax: OverrideMember",
        "(10,32): error TCS1001: unsupported syntax: BaseMemberAccess",
        "(11,5): error TCS1001: unsupported syntax: OverrideMember",
        "(12,5): error TCS1001: unsupported syntax: NewMemberHiding",
        "(15,33): error TCS1001: unsupported syntax: ClassInheritance(R)",
        "(18,5): error TCS1001: unsupported syntax: OverrideMember",
    ];

    [Fact]
    public void Transpile_InheritanceIsBuildBlockingError()
    {
        var result = Transpiler.TranspileWithDiagnostics([Source]);

        Assert.Equal(Expected, result.Errors);
        Assert.Empty(result.Lua);
    }

    [Fact]
    public void Check_InheritanceExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_inheritance_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Source);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));
            Assert.Equal(1, exitCode);
            foreach (var line in Expected)
                Assert.Contains("input.cs" + line, stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // 増分 session (playground / watch) も Lua を出さずに止まる
    [Fact]
    public void IncrementalSession_InheritanceIsError()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("game/A.cs", """
            public class B { }
            public class D : B { }
            """)]);
        var (errors, _) = session.CollectDiagnostics();

        Assert.Contains(errors, e => e.Contains(
            "error TCS1001: unsupported syntax: ClassInheritance(B)"));
        Assert.Equal(0, session.Revision);
        Assert.Empty(session.Artifacts);
    }

    // interface の実装 (class / record / struct)、sealed class、interface の
    // 継承、object の明示は従来どおり
    [Fact]
    public void InterfaceImplementation_IsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public interface IShape { int Area(); }
            public interface ISquare : IShape { int Side(); }
            public sealed class Sq : ISquare
            {
                public int S;
                public int Area() => S * S;
                public int Side() => S;
            }
            public record R(int A) : IShape { public int Area() => A; }
            public class O : object { }
            public static class P
            {
                public static int Test()
                {
                    IShape a = new Sq { S = 3 };
                    IShape b = new R(4);
                    return a.Area() + b.Area();
                }
            }
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
        Assert.Equal("13", TestHelper.RunLua(result.Lua + "\nprint(P.Test())").Trim());
    }

    // --ref の型は host の宣言 (lub の `sealed class LubHandleAttribute :
    // Attribute`、stub の class 継承) で、診断の対象にしない
    [Fact]
    public void RefStubInheritance_IsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public static class P
            {
                public static int Test() => Host.Make().Size + Host.Make().Extra;
            }
            """], referenceSources: ["""
            using System;
            [AttributeUsage(AttributeTargets.Class)]
            public sealed class HostHandleAttribute : Attribute { }
            public class BaseData { public int Size; }
            public class MoreData : BaseData { public int Extra; }
            public abstract class Node { public virtual int F() => 0; }
            public static class Host { public static extern MoreData Make(); }
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}
