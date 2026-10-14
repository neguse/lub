namespace TinyCs.Tests;

// struct / record struct の static field と static auto property はサブセット外。Lua は読むと nil、
// C は止まるので、transpiler は Lua を出さない error、check は exit 1 にする (#154)。
// const は使用箇所で値に畳まれるので対象外
[Collection(ConsoleCollection.Name)]
public class StructStaticFieldTests
{
    private const string Source = """
        public struct S { public int V; public static int X = 1; }
        public record struct RS(int a) { public static readonly int Y = 2; }
        public struct T { public int V; public static int Count { get; set; } = 3; }
        public static class P
        {
            public static int Test() => S.X + RS.Y + T.Count;
        }
        """;

    [Fact]
    public void Transpile_StructStaticFieldIsBuildBlockingError()
    {
        var result = Transpiler.TranspileWithDiagnostics([Source]);

        Assert.Equal(3, result.Errors.Count);
        Assert.All(result.Errors, e => Assert.Contains(
            "error TCS1001: unsupported syntax: StructStaticField", e));
        Assert.StartsWith("(1,33)", result.Errors[0]);
        Assert.StartsWith("(2,34)", result.Errors[1]);
        Assert.StartsWith("(3,33)", result.Errors[2]);
    }

    [Fact]
    public void Check_StructStaticFieldExits1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_struct_static_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Source);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));
            Assert.Equal(1, exitCode);
            Assert.Contains("input.cs(1,33): error TCS1001: unsupported syntax: StructStaticField",
                stderr);
            Assert.Contains("input.cs(2,34): error TCS1001: unsupported syntax: StructStaticField",
                stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    // 本体を持つ static property は呼ぶと Lua が nil 呼び出しで止まるので、
    // 従来どおり warning の StructMember
    [Fact]
    public void StructStaticComputedProperty_IsStructMemberError()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct S { public int V; public static int Two => 2; }
            """]);

        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("StructMember(PropertyDeclaration)"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("StructStaticField"));
    }

    [Fact]
    public void StructConstIsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public struct S { public int V; public const int K = 5; }
            public record struct RS(int a) { public const int K = 3; }
            public static class P
            {
                public static int Test() => S.K + RS.K;
            }
            """]);

        Assert.Empty(result.Errors);
        Assert.Empty(result.Warnings);
    }
}
