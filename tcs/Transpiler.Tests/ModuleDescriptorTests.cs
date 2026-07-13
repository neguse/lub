using TinyCs;

namespace TinyCs.Tests;

// doc/incremental-module-compilation-design.md §10-§11 (M2) の受入テスト。
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
        Assert.Contains("Counter.Tag = nil", init);

        Assert.Equal(["__index", "new", "Bump", "Gone"], counter.DefinitionKeys);
        Assert.Collection(counter.StaticFields,
            s =>
            {
                Assert.Equal("Count", s.Key);
                Assert.Equal("0", s.DefaultLua);
                Assert.True(s.Pure); // 定数 initializer
            },
            s =>
            {
                Assert.Equal("Tag", s.Key);
                Assert.Equal("nil", s.DefaultLua);
                Assert.True(s.Pure); // initializer なし = default
            });

        var suit = artifact.Types.Single(t => t.Name == "Suit");
        Assert.Equal("enum", suit.Kind);
        Assert.Equal(["Hearts", "Spades"], suit.DefinitionKeys);
    }

    [Fact]
    public void FreshSnapshotRunsEntryViaWrapper()
    {
        // 継承を module 順と逆にする (declare 先行で link できること §11.1)
        var session = Open(
            ("game.cs", """
                public class Game : Base
                {
                    public static int Run() { return Helper.Twice(Score()) + Suit(); }
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
        // A.x の initializer が B.y を先読みする。pre-zero により nil ではなく
        // 型 default (0) を読む (§11.1)。
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
        Assert.Equal(["1", "11"],
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
}
