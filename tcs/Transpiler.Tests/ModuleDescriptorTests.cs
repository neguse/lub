using TinyCs;

namespace TinyCs.Tests;

// doc/incremental-module-compilation-design.md §10-§11 の受入テスト。
// descriptor 分割 (declare/define/initializers) と、LinkSnapshot 出力を実 Lua VM
// で動かした fresh apply / hot apply の registry 挙動を検証する。
public class ModuleDescriptorTests
{
    private static string TinySystemLua =>
        LuaRuntime.LoadTinySystemSource();

    private static string RegistryLua =>
        LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath);

    private static IncrementalCompilationSession Open(
        params (string Path, string Text)[] files)
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject(files);
        return session;
    }

    private static string Snapshot(IncrementalCompilationSession session,
        string? entry = null) =>
        ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision, entry,
            TinySystemLua, RegistryLua);

    // snapshot を一時ファイルへ書き、script (dofile ベース) を実行する
    private static string RunWithSnapshots(string script,
        params string[] snapshots)
    {
        var files = snapshots
            .Select(s =>
            {
                var f = Path.GetTempFileName();
                File.WriteAllText(f, s);
                return f.Replace("\\", "/");
            })
            .ToArray();
        try
        {
            var header = string.Join("\n",
                files.Select((f, i) => $"local snap{i + 1} = \"{f}\""));
            return TestHelper.RunLua(header + "\n" + script).Trim();
        }
        finally
        {
            foreach (var f in files)
                File.Delete(f);
        }
    }

    private const string CounterCs = """
        public class Counter
        {
            public static int Count = 100;
            public static string Tag;
            public int value = 7;
            public static int Bump() { Count = Count + 1; return Count; }
            public static int Gone() { return -1; }
        }
        public enum Suit { Hearts, Spades = 10 }
        """;

    [Fact]
    public void DefineChunkExcludesDeclarationAndStaticInit()
    {
        var session = Open(("counter.cs", CounterCs));
        var artifact = session.Artifacts.Single();

        var define = ModuleArtifactText.BuildDefineLua(
            artifact.RawLua, artifact.Types);
        Assert.DoesNotContain("Counter = {}", define);
        Assert.DoesNotContain("Counter.Count = 100", define);
        Assert.DoesNotContain("Suit = {}", define);
        Assert.Contains("Counter.__index = Counter", define);
        Assert.Contains("function Counter.Bump()", define);
        Assert.Contains("Suit.Spades = 10", define); // enum member は define 側

        var counter = artifact.Types.Single(t => t.Name == "Counter");
        var init = ModuleArtifactText.BuildTypeInitializerLua(
            artifact.RawLua, counter);
        Assert.Contains("Counter.Count = 100", init);
        // initializer のない static は pre-zero の default だけで、initializer
        // thunk では代入しない (先の initializer が書いた値を戻さない)
        Assert.DoesNotContain("Counter.Tag", init);

        Assert.Equal(["__index", "new", "Bump", "Gone"], counter.DefinitionKeys);
        Assert.Collection(counter.StaticFields,
            s =>
            {
                Assert.Equal("Count", s.Key);
                Assert.Equal("0", s.DefaultLua);
            },
            s =>
            {
                Assert.Equal("Tag", s.Key);
                Assert.Equal("nil", s.DefaultLua);
            });

        var suit = artifact.Types.Single(t => t.Name == "Suit");
        Assert.Equal("enum", suit.Kind);
        Assert.Equal(["Hearts", "Spades"], suit.DefinitionKeys);
    }

    // out _ (bare discard) は既存 local への out ではないので、out var と
    // 同じく statement 前の local 宣言が要る。宣言を落とすと `_` への global
    // 書き込みになり、plain 実行では黙って通るが undeclared global write を
    // 拒否する module _ENV では実行時 error になる。
    [Fact]
    public void OutDiscardGetsLocalPreDeclUnderModuleEnv()
    {
        var session = new IncrementalCompilationSession(
            [
                """
                public static class Loader
                {
                    public static void load_text(string path, out string text,
                        out int version, out string status)
                    {
                        text = "";
                        version = 0;
                        status = "";
                    }
                }
                """,
            ]);
        session.OpenProject([("game.cs", """
            public class Game
            {
                public static int Run()
                {
                    Loader.load_text("x", out _, out var version, out _);
                    return version;
                }
            }
            """)]);
        Assert.Empty(session.CollectDiagnostics().Errors);

        var output = RunWithSnapshots(
            """
            loader = { load_text = function() return "t", 7, "s" end }
            local w = dofile(snap1)
            print(w.Run())
            """,
            Snapshot(session, "Game"));
        Assert.Equal("7", output);
    }

    [Fact]
    public void FreshSnapshotRunsEntryViaWrapper()
    {
        // 参照先の型を module 順と逆にする (declare 先行で link できること §11.1)
        var session = Open(
            ("game.cs", """
                public class Game
                {
                    public static int Run() { return Helper.Twice(Base.Score()) + Suit(); }
                    public static int Suit() { return 0; }
                }
                """),
            ("lib.cs", """
                public class Base
                {
                    public static int Score() { return 21; }
                }
                public class Helper
                {
                    public static int Twice(int x) { return x * 2; }
                }
                """));
        Assert.Empty(session.CollectDiagnostics().Errors);

        var output = RunWithSnapshots(
            """
            local w = dofile(snap1)
            print(w.Run())
            """,
            Snapshot(session, "Game"));
        Assert.Equal("42", output);
    }

    [Fact]
    public void HotApplyKeepsIdentityAndSwapsMethodBody()
    {
        var vec = """
            public class Vec
            {
                public int x = 3;
                public static int Made = 0;
                public int Get() { return x; }
            }
            """;
        var session = Open(("vec.cs", vec));
        var snap1 = Snapshot(session, "Vec");

        var r = session.Update("vec.cs", vec.Replace("return x;", "return x * 10;"));
        Assert.True(r.Success && r.FastPath);
        var snap2 = Snapshot(session, "Vec");

        var output = RunWithSnapshots(
            """
            local w = dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local Vec = reg.types["vec.cs#Vec"]
            Vec.Made = 5 -- 実行中に変わった static 値
            local inst = Vec.new()
            print(inst:Get())
            local w2 = dofile(snap2)
            print(w == w2)                       -- wrapper identity
            print(Vec == reg.types["vec.cs#Vec"]) -- type table identity
            print(getmetatable(inst) == Vec)      -- instance metatable identity
            print(inst:Get())                     -- 既存 instance に新 body
            print(Vec.Made)                       -- static は method-body edit で保持
            """,
            snap1, snap2);
        Assert.Equal(["3", "true", "true", "true", "30", "5"],
            output.Split('\n').Select(l => l.Trim()));
    }

    // static initializer が自クラスの ctor / static method を呼ぶ (#15)。
    // define チャンク (new / method) の後に type 単位の initializer thunk が
    // 走り、method-body edit の hot apply では static が保持される
    [Fact]
    public void StaticInitializerCallingOwnConstructorLoadsAndSurvivesHotApply()
    {
        var vec = """
            public class Vec
            {
                public static Vec Zero = new Vec(0);
                public static int Seed = Compute();
                public int x;
                public Vec(int v) { x = v; }
                public static int Compute() { return 3; }
                public int Get() { return x; }
            }
            """;
        var session = Open(("vec.cs", vec));
        var snap1 = Snapshot(session, "Vec");

        var r = session.Update("vec.cs", vec.Replace("return x;", "return x * 10;"));
        Assert.True(r.Success && r.FastPath, string.Join(";", r.RestartReasons));
        var snap2 = Snapshot(session, "Vec");

        var output = RunWithSnapshots(
            """
            dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local Vec = reg.types["vec.cs#Vec"]
            print(Vec.Zero:Get(), Vec.Seed)
            Vec.Zero.x = 4
            local zero = Vec.Zero
            dofile(snap2)
            print(Vec.Zero == zero, Vec.Zero:Get(), Vec.Seed)
            """,
            snap1, snap2);
        Assert.Equal(["0\t3", "true\t40\t3"],
            output.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void HotApplySkipsUnchangedModulesAndDeletesRemovedKeys()
    {
        var session = Open(("counter.cs", CounterCs),
            ("lib.cs", "public class Lib { public static int Id(int x) { return x; } }"));
        var snap1 = Snapshot(session);

        // Gone() を削除し、新しい pure static を足す (surface change → slow path)
        var v2 = CounterCs
            .Replace("public static int Gone() { return -1; }", "")
            .Replace("public static string Tag;",
                "public static string Tag;\n    public static int Extra = 9;");
        var r = session.Update("counter.cs", v2);
        Assert.True(r.Success);
        Assert.False(r.FastPath);
        var snap2 = Snapshot(session);

        var output = RunWithSnapshots(
            """
            dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local lib_id = reg.types["lib.cs#Lib"].Id
            local counter = reg.types["counter.cs#Counter"]
            counter.Count = 500
            dofile(snap2)
            print(reg.types["lib.cs#Lib"].Id == lib_id) -- unchanged module は skip
            print(counter.Gone)                          -- 削除 key は消える
            print(counter.Extra)                         -- 新規 pure static は初期化
            print(counter.Count)                         -- 既存 static は保持
            """,
            snap1, snap2);
        Assert.Equal(["true", "nil", "9", "500"],
            output.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void PreZeroMakesCrossTypeInitReadDefaultsNotNil()
    {
        // A.x と B.Y の initializer が互いを読む循環。先に宣言した A を先に
        // 触ったときの lazy 初期化と同じく B を先に初期化し、B は初期化中の
        // A.x を pre-zero の型 default (0) で読む (nil ではない、§11.1)。
        var session = Open(
            ("a.cs", "public class A { public static int X = B.Y + 1; }"),
            ("b.cs", "public class B { public static int Y = A.X + 10; }"));
        Assert.Empty(session.CollectDiagnostics().Errors);

        var output = RunWithSnapshots(
            """
            dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            print(reg.types["a.cs#A"].X)
            print(reg.types["b.cs#B"].Y)
            """,
            Snapshot(session));
        Assert.Equal(["11", "10"],
            output.Split('\n').Select(l => l.Trim()));
    }

    [Fact]
    public void ModuleEnvRejectsUndeclaredGlobalWrite()
    {
        var registryPath = TestHelper.FindProjectFile(
            LuaRuntime.RegistryRelativePath);
        var output = TestHelper.RunLua($$"""
            local M = dofile("{{registryPath}}")
            local reg = M.new(_G)
            local ok, err = pcall(function()
                reg:applyBatch({
                    revision = 1,
                    modules = { {
                        id = "m", hash = "h",
                        types = { { id = "m#T", name = "T", kind = "class",
                            statics = {}, keys = {} } },
                        define = function(_ENV) Rogue = 1 end,
                        inits = {}, initfns = {},
                    } },
                })
            end)
            print(ok, err)
            """).Trim();
        Assert.StartsWith("false", output);
        Assert.Contains("undeclared global", output);
    }

    // struct / record struct も registry の runtime type (#16)。宣言行は
    // declare 側に回り、define は module env 経由で stable table に member を
    // 載せる。descriptor に無いと `P = {}` が undeclared global write になる
    [Fact]
    public void StructIsDeclaredRuntimeType()
    {
        var session = Open(("p.cs", """
            public struct P
            {
                public int X;
                public int Doubled() { return X * 2; }
            }
            public record struct Q(int A, int B);
            public class App
            {
                public static int Run()
                {
                    var p = new P();
                    p.X = 2;
                    var q = new Q(3, 4);
                    return p.X + p.Doubled() + (q == new Q(3, 4) ? q.B : 0);
                }
            }
            """));
        Assert.Empty(session.CollectDiagnostics().Errors);
        var artifact = session.Artifacts.Single();

        var p = artifact.Types.Single(t => t.Name == "P");
        Assert.Equal("struct", p.Kind);
        Assert.Contains("new", p.DefinitionKeys);
        Assert.Contains("__copy", p.DefinitionKeys);
        Assert.Contains("op_Equality", p.DefinitionKeys);
        Assert.Contains("Doubled", p.DefinitionKeys);
        var q = artifact.Types.Single(t => t.Name == "Q");
        Assert.Equal("struct", q.Kind);
        Assert.Contains("ctor", q.DefinitionKeys);

        var define = ModuleArtifactText.BuildDefineLua(artifact.RawLua, artifact.Types);
        Assert.DoesNotContain("P = {}", define);
        Assert.DoesNotContain("Q = {}", define);
        Assert.Contains("function P.new()", define);

        var output = RunWithSnapshots(
            """
            local w = dofile(snap1)
            print(w.Run())
            """,
            Snapshot(session, "App"));
        Assert.Equal("10", output);
    }

    // struct method の body edit は hot apply で既存の値にも届く (呼び出し側は
    // module env 経由で stable type table を引く)。field の追加も live (値は移行する)
    [Fact]
    public void StructMethodBodyEditHotAppliesAndShapeChangeIsLive()
    {
        var src = """
            public struct P
            {
                public int X;
                public int Get() { return X; }
            }
            public class App
            {
                public static P Make() { var p = new P(); p.X = 3; return p; }
                public static int Read(P p) { return p.Get(); }
            }
            """;
        var session = Open(("p.cs", src));
        var snap1 = Snapshot(session, "App");

        var r = session.Update("p.cs", src.Replace("return X;", "return X * 10;"));
        Assert.True(r.Success, string.Join(";", r.Errors));
        Assert.False(r.RequiresRestart, string.Join(";", r.RestartReasons));
        var snap2 = Snapshot(session, "App");

        var output = RunWithSnapshots(
            """
            local w = dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local P = reg.types["p.cs#P"]
            local p = w.Make()
            print(w.Read(p))
            dofile(snap2)
            print(P == reg.types["p.cs#P"], w.Read(p))
            """,
            snap1, snap2);
        Assert.Equal(["3", "true\t30"],
            output.Split('\n').Select(l => l.Trim()));

        var r2 = session.Update("p.cs", src.Replace("public int X;",
            "public int X;\n    public int Y;"));
        Assert.True(r2.Success);
        Assert.False(r2.RequiresRestart, string.Join(";", r2.RestartReasons));
    }

    // default 値が同じ (nil) 型への field 型変更も live。型の変わった field は、移行で
    // 新しい型の default に戻る (旧い型の値のまま新しい body が読まない)
    [Fact]
    public void StructFieldTypeChangeWithSameDefaultIsLive()
    {
        var src = """
            public struct P
            {
                public string X;
                public int Read() { return X.Length + 1; }
            }
            public class App
            {
                public static P Make() { var p = new P(); p.X = "a"; return p; }
            }
            """;
        var session = Open(("p.cs", src));
        Assert.Empty(session.CollectDiagnostics().Errors);

        var r = session.Update("p.cs", src
            .Replace("public string X;", "public int[] X;")
            .Replace("X.Length + 1", "X[0] + 1")
            .Replace("p.X = \"a\";", "p.X = new int[] { 1 };"));
        Assert.True(r.Success, string.Join(";", r.Errors));
        Assert.False(r.RequiresRestart, string.Join(";", r.RestartReasons));
    }

    [Fact]
    public void StaleRevisionIsSkipped()
    {
        var session = Open(("t.cs",
            "public class T { public static int V() { return 1; } }"));
        var snap1 = Snapshot(session);
        var output = RunWithSnapshots(
            """
            dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local before = reg.revision
            dofile(snap1) -- 同 revision の再適用は skip
            print(before == reg.revision)
            """,
            snap1);
        Assert.Equal("true", output);
    }

    // 初期化子のない static を既存 type に足す hot apply: registry の declare が
    // 型別 default で pre-zero する。既存の初期化子なし static は live 値のまま
    [Fact]
    public void HotApplyAddedStaticWithoutInitializerGetsDefault()
    {
        var v1 = """
            public class Counter
            {
                public static int First = Init();
                public static int Later;
                static int Init() { Later = 7; return 3; }
                public static void Bump() { Later++; }
            }
            """;
        var session = Open(("counter.cs", v1));
        var snap1 = Snapshot(session);
        var r = session.Update("counter.cs", v1
            .Replace("public static int Later;",
                "public static int Later;\n    public static int Hits;\n    public static bool Seen;")
            .Replace("{ Later++; }", "{ Hits++; Later++; }"));
        Assert.True(r.Success);
        Assert.False(r.RequiresRestart, string.Join(";", r.RestartReasons));
        var snap2 = Snapshot(session);

        var output = RunWithSnapshots(
            """
            dofile(snap1)
            local reg = _G.__tcs_module_runtime.registry
            local counter = reg.types["counter.cs#Counter"]
            print(counter.Later)  -- initializer の書き込みが残る
            counter.Later = 20
            dofile(snap2)
            counter.Bump()
            print(counter.Hits)   -- 追加 static は default から
            print(counter.Seen)
            print(counter.Later)  -- 既存 static は保持
            """,
            snap1, snap2);
        Assert.Equal(["7", "1", "false", "21"],
            output.Split('\n').Select(l => l.Trim()));
    }
}
