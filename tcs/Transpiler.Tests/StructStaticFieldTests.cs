namespace TinyCs.Tests;

// struct / record struct の static field と static auto property は class と同じ
// (型の table に pre-zero し、initializer は全型の定義の後)。実行結果は
// DifferentialTests.StructOperators が押さえる。ここは build の可否
public class StructStaticFieldTests
{
    [Fact]
    public void StructStaticField_IsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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

    // C# は閉じた型ごとに別の値を持つが、Lua は型を消すので 1 つを共有してしまう
    [Fact]
    public void GenericStructStaticField_IsBuildBlockingError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct S<T> { public T V; public static int X = 1; }
            """]);

        Assert.Contains(result.Errors, e => e.Contains(
            "error TCS1001: unsupported syntax: GenericStaticField"));
    }
}
