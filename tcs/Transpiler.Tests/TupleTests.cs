namespace TinyCs.Tests;

public class TupleTests
{
    private static TranspileResult Run(string body) => Transpiler.TranspileWithDiagnostics([$$"""
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

    private static string FunctionBody(string lua, string name)
    {
        var start = lua.IndexOf($"function {name}", StringComparison.Ordinal);
        var end = lua.IndexOf("\nend", start, StringComparison.Ordinal);
        return lua[start..end];
    }

    // 分解は multi-return で受け、struct の table を作らない
    [Fact]
    public void ReturnThenDeconstruct_DoesNotAllocate()
    {
        var result = Run("var (a, b) = Two(); Console.WriteLine(a + b);");

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("return 1, 2", FunctionBody(result.Lua, "P.Two"));
        Assert.Contains("local a, b = P.Two()", FunctionBody(result.Lua, "P.Run"));
    }

    [Fact]
    public void Swap_DoesNotAllocate()
    {
        var result = Run("int a = 1, b = 2; (a, b) = (b, a); Console.WriteLine(a);");

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Contains("a, b = b, a", FunctionBody(result.Lua, "P.Run"));
    }

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
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class C
            {
                public void Deconstruct(out int a, out int b) { a = 1; b = 2; }
                public static int Run() { var (x, y) = new C(); return x + y; }
            }
            """]);

        Assert.False(result.Success);
    }

    [Fact]
    public void WithoutTuple_LibraryIsNotEmitted()
    {
        var result = Transpiler.TranspileWithDiagnostics(["public class P { public static int Run() { return 1; } }"]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.DoesNotContain("ValueTuple", result.Lua);
    }

    [Fact]
    public void IncrementalSession_TupleAcrossModules()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("A.cs", "public static class A { public static (int x, string y) Two() { return (1, \"a\"); } }"),
            ("B.cs", """
                public static class B
                {
                    public static string Run() { var t = A.Two(); var (n, s) = A.Two(); return t.y + (t.x + n) + s; }
                }
                """),
        ]);

        Assert.Equal(["TinySystem/ValueTuple.cs", "A.cs", "B.cs"], session.Artifacts.Select(a => a.ModuleId));
        // module の chunk は別々に読み込まれ、prelude の local を chunk ごとに持つ
        var lua = string.Join("", session.Artifacts.Select(a => $"do\n{a.Lua}\nend\n"));
        Assert.Equal("a2a", TestHelper.RunLua($"{lua}\nprint(B.Run())").Trim());
    }
}
