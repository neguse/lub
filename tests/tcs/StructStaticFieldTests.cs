namespace TinyCs.Tests;

// Static fields and static auto properties of struct / record struct are the same as for classes
// (pre-zeroed in the type's table, initializers after all type definitions). Execution results are
// covered by DifferentialTests.StructOperators. This file checks whether it builds
public class StructStaticFieldTests
{
    [Fact]
    public void StructStaticField_IsNotDiagnosed()
    {
        var result = Checker.Check(["""
            public struct S { public int V; public static int X = 1; public const int K = 5; }
            public record struct RS(int a) { public static readonly int Y = 2; }
            public struct T { public int V; public static int Count { get; set; } = 3; }
            public static class P
            {
                public static int Test() => S.X + S.K + RS.Y + T.Count;
            }
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }

    // C# has a separate value per closed type, but Lua erases types, so one value ends up shared
    [Fact]
    public void GenericStructStaticField_IsBuildBlockingError()
    {
        var result = Checker.Check(["""
            public struct S<T> { public T V; public static int X = 1; }
            """]);

        Assert.Contains(result.Errors, e => e.Contains(
            "error TCS1001: unsupported syntax: GenericStaticField"));
    }
}
