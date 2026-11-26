using TinyCs;

namespace TinyCs.Tests;

// Diagnostics for static initializers. Execution results of initialization order: DifferentialTests.StaticInitOrder.cs in tests/tcs-backends (C / VM / .NET)
public class StaticInitOrderTests
{
    // Initializers are built together after the types, but their diagnostics also appear in the result
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

    // sizeof in a method body is also stopped with the same diagnostic (a body that cannot be made into IL)
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
