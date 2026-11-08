using TinyCs;

namespace TinyCs.Tests;

// static initializer の診断。初期化の順の実行結果は tcs2c.Tests の DifferentialTests.StaticInitOrder.cs (C / VM / .NET)
public class StaticInitOrderTests
{
    // initializer は型の後にまとめて組み立てるが、その診断も結果に載る
    [Fact]
    public void DeferredInitializerDiagnosticsAreReported()
    {
        var result = Checker.Check(["""
            public class Test
            {
                public static int A = 1;
                public static int H = sizeof(int) * A;
            }
            """]);
        Assert.Contains(result.Errors, e => e.Contains("SizeOfExpression"));
    }

    // method の本文の sizeof も同じ診断で止める (IL にできない本文)
    [Fact]
    public void MethodBodySizeOfIsReported()
    {
        var result = Checker.Check([
            "public class A { public static int F() { return sizeof(int); } }",
            "public class B { public static int G() { return A.F(); } }",
        ]);
        Assert.Contains(result.Errors, e => e.Contains("SizeOfExpression"));
    }
}
