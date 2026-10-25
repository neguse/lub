using TinyCs;

namespace TinyCs.Tests;

// static initializer の初期化順 (StaticInitOrder)。単一 chunk の Lua / C の
// 実行結果は tcs2c.Tests の DifferentialTests.StaticInitOrder.cs。module mode
// (registry) では fresh VM で全型を declare / define してから、initializer が
// 読む他の型 (他 module の method 本文の先も含む) を先に初期化する
public class StaticInitOrderTests
{
    // initializer は全型の後にまとめて出すが、その診断も結果に載る
    [Fact]
    public void DeferredInitializerDiagnosticsAreReported()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Test
            {
                public static int A = 1;
                public static int H = sizeof(int) * A;
            }
            """]);
        Assert.Contains(result.Errors, e => e.Contains("SizeOfExpression"));
    }

    internal static string Snapshot(IncrementalCompilationSession session) =>
        ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision, null,
            LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));

    internal static string[] RunSnapshot(string snapshot, string script)
    {
        var file = Path.GetTempFileName();
        File.WriteAllText(file, snapshot);
        try
        {
            return TestHelper.RunLua(
                    $"dofile(\"{file.Replace("\\", "/")}\")\n" +
                    "local reg = _G.__tcs_module_runtime.registry\n" + script)
                .Trim().Split('\n').Select(l => l.Trim()).ToArray();
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void FreshSnapshotInitializesReferencedTypesFirst()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", """
                public class A
                {
                    public static B Inst = new B(5);
                    public static int K = B.Base + 1;
                    public static int L = Lib.Get();
                }
                """),
            ("b.cs", """
                public class B
                {
                    public static int Base = 10;
                    public int V;
                    public B(int v) { V = v; }
                }
                """),
            ("lib.cs", "public class Lib { public static int Get() { return Z.W * 2; } }"),
            ("z.cs", "public class Z { public static int W = B.Base + 11; }"),
        ]);
        var output = RunSnapshot(Snapshot(session), """
            local A = reg.types["a.cs#A"]
            print(A.Inst.V + A.K)
            print(A.L)
            """);
        Assert.Equal(["16", "42"], output);
    }

    // 依存は他 module の method 本文から来るので、fast path (body edit) で
    // その本文を変えても次の fresh VM は新しい依存の順で初期化する
    [Fact]
    public void BodyEditInAnotherModuleUpdatesInitOrder()
    {
        var lib = "public class Lib { public static int Get() { return 1; } }";
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", "public class A { public static int X = Lib.Get(); }"),
            ("lib.cs", lib),
            ("z.cs", "public class Z { public static int W = Seed(); static int Seed() { return 7; } }"),
        ]);
        var r = session.Update("lib.cs", lib.Replace("return 1;", "return Z.W;"));
        Assert.True(r.Success && r.FastPath);
        var output = RunSnapshot(Snapshot(session),
            "print(reg.types[\"a.cs#A\"].X)");
        Assert.Equal(["7"], output);

        // 触る型・呼ぶ method が変わらない編集では依存を取り直さず引き継ぐ
        r = session.Update("lib.cs", lib.Replace("return 1;", "return Z.W * 1;"));
        Assert.True(r.Success && r.FastPath);
        output = RunSnapshot(Snapshot(session),
            "print(reg.types[\"a.cs#A\"].X)");
        Assert.Equal(["7"], output);
    }

    // fast path でも method 単位の差し替えができない編集 (constructor 本文など)
    // は module を丸ごと emit し直す。その module の型の依存を失わない
    [Fact]
    public void ConstructorBodyEditKeepsInitDependencies()
    {
        var a = """
            public class A
            {
                public static int K = B.Base + 1;
                public int V;
                public A() { V = 1; }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", a),
            ("b.cs", "public class B { public static int Base = 10; }"),
        ]);
        var r = session.Update("a.cs", a.Replace("V = 1;", "V = 2;"));
        Assert.True(r.Success && r.FastPath);
        var output = RunSnapshot(Snapshot(session),
            "print(reg.types[\"a.cs#A\"].K)");
        Assert.Equal(["11"], output);
    }

    // struct 型の static の pre-zero は descriptor の構築時 (型の定義より前) に
    // 評価されるので、struct の module が後ろでも落ちない
    [Fact]
    public void StructStaticBeforeStructModule()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("a.cs", """
                public class A
                {
                    public static V2 P = new V2(1, 2);
                    public static V2 Q;
                }
                """),
            ("v2.cs", "public struct V2 { public int X; public int Y; public V2(int x, int y) { X = x; Y = y; } }"),
        ]);
        var output = RunSnapshot(Snapshot(session), """
            local A = reg.types["a.cs#A"]
            print(A.P.X + A.P.Y, A.Q.X, A.Q.Y)
            """);
        Assert.Equal(["3\t0\t0"], output);
    }
}
