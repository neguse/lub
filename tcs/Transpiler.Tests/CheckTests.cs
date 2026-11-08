namespace TinyCs.Tests;

// tcs check (Session.CheckOnly) は Lua を出さず、IL を組み立てて、組み立てられない本文と式を TCS1001 にする。
// `as` の先が class の式は準拠の検査を通るが IL にできない形 (組み立ての内部の誤りに理由が残る)
public class CheckTests
{
    [Fact]
    public void UnbuildableBodies_AreUnsupportedSyntaxInFileAndPositionOrder()
    {
        var result = Checker.Check(["""
            public class Foo { }
            public class P
            {
                public static bool G(object x) { return x as Foo != null; }
                static object o = new Foo();
                static Foo? f = o as Foo;
                public bool H(object x) => x as Foo != null;
            }
            """], ["A.cs"]);

        Assert.Equal(
        [
            "A.cs(4,36): error TCS1001: unsupported syntax: Block",
            "A.cs(6,21): error TCS1001: unsupported syntax: As",
            "A.cs(7,32): error TCS1001: unsupported syntax: Binary",
            "internal: IlBuild: as (As at A.cs:6)",
            "internal: IlBuild: as (As at A.cs:4)",
            "internal: IlBuild: as (As at A.cs:7)",
        ], result.Errors);
    }

    // top-level の文は文ごとに組み立て、IL にできない文をそれぞれ報告する
    [Fact]
    public void UnbuildableTopLevelStatements_AreReportedOneByOne()
    {
        var result = Checker.Check(["""
            public class Foo { }
            """, """
            object o = new Foo();
            var a = o as Foo;
            System.Console.WriteLine(a == null);
            var b = o as Foo;
            """], ["Foo.cs", "Main.cs"]);

        Assert.Equal(2, result.Errors.Count(e => e.Contains("error TCS1001: unsupported syntax: LocalDecl")));
        Assert.Contains(result.Errors, e => e.StartsWith("Main.cs(2,1): error TCS1001"));
        Assert.Contains(result.Errors, e => e.StartsWith("Main.cs(4,1): error TCS1001"));
    }

    // 式の本文の constructor も IL にする (組み立てられなければ報告する)
    [Fact]
    public void ExpressionBodiedConstructor_IsBuilt()
    {
        var ok = Checker.Check(["""
            public class P { int x; public P(int a) => x = a; public int X() => x; }
            """]);
        var bad = Checker.Check(["""
            public class Foo { }
            public class P { Foo? f; public P(object o) => f = o as Foo; }
            """], ["A.cs"]);

        Assert.True(ok.Success, string.Join("\n", ok.Errors));
        Assert.Contains("A.cs(2,48): error TCS1001: unsupported syntax: Assign", bad.Errors);
    }

    [Theory]
    [InlineData("Missing", "entry class not found: Missing")]
    [InlineData("IApp", "entry class must be a class or record class: IApp")]
    public void EntryClass_IsValidated(string entry, string error)
    {
        var result = Checker.Check(["""
            public interface IApp { }
            public class App : IApp { }
            """], entryClass: entry);

        Assert.Equal([error], result.Errors);
    }
}
