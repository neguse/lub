namespace TinyCs.Tests;

public class SmokeTests
{
    [Fact]
    public void StaticMethod_ReturnsLiteral()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Foo
            {
                public static int Bar() { return 42; }
            }
            """,
            "Foo.Bar()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void StaticMethod_Addition()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Math
            {
                public static int Add(int a, int b) { return a + b; }
            }
            """,
            "Math.Add(3, 7)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void IfElse_TrueBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Logic
            {
                public static int Check(int x)
                {
                    if (x > 0) { return 1; }
                    else { return 0; }
                }
            }
            """,
            "Logic.Check(5)");
        Assert.Equal("1", result);
    }

    [Fact]
    public void IfElse_FalseBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Logic
            {
                public static int Check(int x)
                {
                    if (x > 0) { return 1; }
                    else { return 0; }
                }
            }
            """,
            "Logic.Check(-3)");
        Assert.Equal("0", result);
    }

    [Fact]
    public void BooleanLiterals()
    {
        var result = TestHelper.TranspileAndRun("""
            public class B
            {
                public static bool T() { return true; }
                public static bool F() { return false; }
            }
            """,
            "tostring(B.T()) .. ',' .. tostring(B.F())");
        Assert.Equal("true,false", result);
    }

    [Fact]
    public void LocalVariable()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Calc
            {
                public static int Double(int x)
                {
                    var result = x * 2;
                    return result;
                }
            }
            """,
            "Calc.Double(21)");
        Assert.Equal("42", result);
    }

    [Fact]
    public void WhileLoop()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    var i = 1;
                    while (i <= n)
                    {
                        total = total + i;
                        i = i + 1;
                    }
                    return total;
                }
            }
            """,
            "Loop.Sum(10)");
        Assert.Equal("55", result);
    }

    [Fact]
    public void LogicalOperators()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Logic
            {
                public static bool And(bool a, bool b) { return a && b; }
                public static bool Or(bool a, bool b) { return a || b; }
                public static bool Not(bool a) { return !a; }
            }
            """,
            "tostring(Logic.And(true, false)) .. ',' .. tostring(Logic.Or(true, false)) .. ',' .. tostring(Logic.Not(true))");
        Assert.Equal("false,true,false", result);
    }

    [Fact]
    public void ComparisonOperators()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Cmp
            {
                public static bool Eq(int a, int b) { return a == b; }
                public static bool Neq(int a, int b) { return a != b; }
            }
            """,
            "tostring(Cmp.Eq(1, 1)) .. ',' .. tostring(Cmp.Neq(1, 2))");
        Assert.Equal("true,true", result);
    }

    [Fact]
    public void ElseIf()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Grade
            {
                public static string Eval(int score)
                {
                    if (score >= 90) { return "A"; }
                    else if (score >= 80) { return "B"; }
                    else if (score >= 70) { return "C"; }
                    else { return "F"; }
                }
            }
            """,
            "Grade.Eval(85)");
        Assert.Equal("B", result);
    }
}
