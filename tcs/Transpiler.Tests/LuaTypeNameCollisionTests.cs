namespace TinyCs.Tests;

// namespace 修飾名は `_` で結合する (`A_B.C` と `A.B.C` は共に `A_B_C`)。
// 同じ Lua global 名になる別の型は、member の LuaNameCollision と同じく
// 2 個目以降を error にし、Lua も IL (C backend) も出さない
public class LuaTypeNameCollisionTests
{
    private const string Collision = """
        namespace A_B { public class C { public int V = 1; } }
        namespace A { namespace B { public class C { public int V = 2; } } }
        public static class P { public static void Main() { System.Console.WriteLine(new A_B.C().V + " " + new A.B.C().V); } }
        """;

    private const string Message =
        "error TCS1001: unsupported syntax: LuaNameCollision(A_B.C/A.B.C): " +
        "'A_B.C' and 'A.B.C' both map to Lua 'A_B_C'";

    [Fact]
    public void Transpile_NamespaceJoinCollision_IsErrorWithoutLua()
    {
        var result = Transpiler.TranspileWithDiagnostics([Collision], ["game.cs"]);

        var error = Assert.Single(result.Errors);
        Assert.Equal($"game.cs(2,42): {Message}", error);
        Assert.Equal("", result.Lua);
    }

    [Fact]
    public void Transpile_CollisionAcrossFiles_ReportsLaterFile()
    {
        var result = Transpiler.TranspileWithDiagnostics([
            "namespace A.B { public class C { } }",
            "namespace A_B { public enum C { X } }",
        ], ["first.cs", "second.cs"]);

        var error = Assert.Single(result.Errors);
        Assert.StartsWith("second.cs(1,29): error TCS1001: unsupported syntax: " +
            "LuaNameCollision(A.B.C/A_B.C)", error);
    }

    [Fact]
    public void Transpile_TypesWithoutSharedLuaGlobal_AreNotCollisions()
    {
        // partial の別宣言は同じ型、interface は Lua に出ない、--ref の型は
        // 小文字パス (a_b.c) で呼ぶので user 型の global と重ならない
        var result = Transpiler.TranspileWithDiagnostics([
            "namespace A.B { public partial class C { public int V; } public interface I { } }",
            "namespace A.B { public partial class C { public int W; } }",
            "namespace A_B { public class I { } }",
        ], ["a.cs", "b.cs", "c.cs"],
            referenceSources: ["namespace A_B { public class C { public int Z; } }"],
            checkNaming: false);

        Assert.Empty(result.Errors);
    }

    [Fact]
    public void Check_NamespaceJoinCollision_IsErrorExit1()
    {
        var dir = Directory.CreateTempSubdirectory("tcs_type_collision_");
        try
        {
            var input = Path.Combine(dir.FullName, "input.cs");
            File.WriteAllText(input, Collision);
            var (exitCode, _, stderr) =
                ConsoleCapture.Run(() => Program.Main(["check", input]));

            Assert.Equal(1, exitCode);
            Assert.Contains(Message, stderr);
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public void Session_NamespaceJoinCollision_IsError()
    {
        var session = new IncrementalCompilationSession(checkNaming: false);
        session.OpenProject([
            ("game/One.cs", "namespace A_B { public class C { } }"),
            ("game/Two.cs", "namespace A.B { public class C { } }"),
        ]);

        Assert.Contains(session.CollectDiagnostics().Errors, e =>
            e.StartsWith("game/Two.cs(1,30): error TCS1001: unsupported syntax: " +
                "LuaNameCollision(A_B.C/A.B.C)", StringComparison.Ordinal));
    }

    [Fact]
    public void IlExport_NamespaceJoinCollision_IsDiagnostic()
    {
        // C backend (tcs2c) は IlExport の Diagnostics で止まる
        var exported = IlExport.Export([Collision], specializeGenerics: true,
            sourcePaths: ["game.cs"]);

        Assert.Equal($"game.cs(2,42): {Message}", Assert.Single(exported.Diagnostics));
    }
}
