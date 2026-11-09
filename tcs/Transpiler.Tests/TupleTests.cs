namespace TinyCs.Tests;

public class TupleTests
{
    private static CheckResult Run(string body) => Checker.Check([$$"""
        using System;
        using System.Collections.Generic;
        public struct S { public int N; }
        public class P
        {
            static (int, int) Two() { return (1, 2); }
            public static void Run()
            {
                {{body}}
            }
        }
        """]);

    [Theory]
    [InlineData("var t = (1, 2); Console.WriteLine(t == (1, 2));", "TupleEquality")]
    [InlineData("var t = (1, 2); Console.WriteLine(t != Two());", "TupleEquality")]
    [InlineData("var t = (1, 2); Console.WriteLine(t is (1, 2));", "TuplePattern")]
    [InlineData("var t = (1, 2); switch (t) { case (1, _): break; }", "TuplePattern")]
    [InlineData("var x = 1; Console.WriteLine((x, 2) switch { (1, 2) => 1, _ => 0 });", "TuplePattern")]
    [InlineData("(float, float) f = Two();", "TupleConversion")]
    [InlineData("var t = (1, 2); (float, int) f = t;", "TupleConversion")]
    [InlineData("var t = (1, 2, 3, 4, 5, 6, 7, 8);", "TupleArity")]
    [InlineData("(int, int, int, int, int, int, int, int) t = default;", "TupleArity")]
    [InlineData("var t = new ValueTuple<int, int>(1, 2);", "ValueTupleName")]
    [InlineData("var t = (1, 2); Console.WriteLine(t);", "StructBoxing")]
    [InlineData("var t = (1, 2); Console.WriteLine(\"\" + t);", "StructBoxing")]
    [InlineData("var t = (1, 2); Console.WriteLine($\"{t}\");", "UnsupportedToString")]
    [InlineData("object o = (1, 2);", "StructBoxing")]
    [InlineData("var d = new Dictionary<(int, int), int>();", "ValueEqualityDictionaryKey")]
    public void Unsupported_ReportsTcs1001(string body, string name)
    {
        var result = Run(body);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e =>
            e.Contains($"error {TinyCsDiagnosticIds.UnsupportedSyntax}:") && e.Contains(name));
    }

    [Theory]
    [InlineData("var t = (1, 2); Console.WriteLine(t.ToString());")]
    [InlineData("var t = (1, 2); Console.WriteLine(t.Equals((1, 2)));")]
    [InlineData("var t = (1, 2); Console.WriteLine(t.GetHashCode());")]
    [InlineData("var t = ValueTuple.Create(1, 2);")]
    public void UnsupportedMember_StopsBuild(string body)
    {
        var result = Run(body);

        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
    }

    [Fact]
    public void UserDeconstructMethod_IsNotCalled_StopsBuild()
    {
        var result = Checker.Check(["""
            public class C
            {
                public void Deconstruct(out int a, out int b) { a = 1; b = 2; }
                public static int Run() { var (x, y) = new C(); return x + y; }
            }
            """]);

        Assert.False(result.Success);
    }

    // 同梱の ValueTuple は tuple の構文があるときだけ読む
    [Fact]
    public void WithoutTuple_LibraryIsNotLoaded()
    {
        var without = IlExport.Export(["public class P { public static int Run() { return 1; } }"]);
        Assert.Empty(without.Diagnostics);
        Assert.DoesNotContain(without.Structs, st => st.Name.Contains("ValueTuple"));
        var with = IlExport.Export(["public class P { public static int Run() { var (a, b) = (1, 2); return a + b; } }"]);
        Assert.Empty(with.Diagnostics);
        Assert.Contains(with.Structs, st => st.Name.Contains("ValueTuple"));
    }

    [Fact]
    public void TupleAcrossFiles_Runs()
    {
        var output = TestHelper.CompileAndRunVm([
            "public static class A { public static (int x, string y) Two() { return (1, \"a\"); } }",
            """
                public static class B
                {
                    public static string Run() { var t = A.Two(); var (n, s) = A.Two(); return t.y + (t.x + n) + s; }
                }
                """,
        ], "B.Run()");
        Assert.Equal("a2a", output);
    }
}
