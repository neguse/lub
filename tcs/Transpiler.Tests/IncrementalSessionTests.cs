namespace TinyCs.Tests;

// IncrementalCompilationSession の core の検証
// (doc/incremental-module-compilation-design.md §7-§9, §18.1)。
public class IncrementalSessionTests
{
    private const string FileA = """
        public class Counter
        {
            public int Value;
            public void Add(int amount) { Value = Value + amount; }
            public static int Magic() { return 10; }
        }
        """;

    private const string FileB = """
        public class Game
        {
            public static int Play()
            {
                var c = new Counter();
                c.Add(Counter.Magic());
                return c.Value;
            }
        }
        """;

    private static IncrementalCompilationSession Open()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("game/Counter.cs", FileA), ("game/Game.cs", FileB)]);
        return session;
    }

    private static string LinkAndRun(IncrementalCompilationSession session, string luaExpr)
    {
        var lua = string.Join("", session.Artifacts.Select(a => a.Lua));
        return TestHelper.RunLua($"{lua}\nprint({luaExpr})").Trim();
    }

    private static void AssertDiagnosticsParity(IncrementalCompilationSession session)
    {
        var (errors, warnings) = session.CollectDiagnostics();
        var full = session.BuildFull();
        var left = IncrementalDifferentialTests.Canonicalize(
            new TranspileResult { Errors = errors, Warnings = warnings });
        var right = IncrementalDifferentialTests.Canonicalize(full);
        var diff = IncrementalDifferentialTests.Diff(left, right);
        Assert.True(diff.Length == 0, diff);
    }

    [Fact]
    public void BodyEdit_TakesFastPath_EmitsSingleModule()
    {
        var session = Open();
        var result = session.Update("game/Counter.cs",
            FileA.Replace("return 10;", "return 42;"));
        Assert.True(result.Success);
        Assert.True(result.FastPath);
        Assert.Equal(1, result.ParsedTreeCount);
        Assert.Equal(1, result.EmittedModuleCount);
        Assert.Equal("game/Counter.cs", result.ChangedArtifacts.Single().ModuleId);
        Assert.Equal("42", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void BodyEdit_MatchesFullBuildExecution()
    {
        var session = Open();
        session.Update("game/Counter.cs", FileA.Replace("return 10;", "return 7;"));
        var full = session.BuildFull();
        Assert.True(full.Success);
        var incremental = LinkAndRun(session, "Game.Play()");
        var legacy = TestHelper.RunLua($"{full.Lua}\nprint(Game.Play())").Trim();
        Assert.Equal(legacy, incremental);
        Assert.Equal("7", incremental);
    }

    // instance registry と --trace を付けた session の snapshot は、全体の変換と同じ
    // instance を登録し、同じ行を記録し、同じ型を trace の class に載せる
    [Fact]
    public void InstanceRegistryAndTrace_MatchFullBuild()
    {
        const string game = """
            public class Enemy
            {
                public int Hp = 3;
                public void Hit() { Hp = Hp - 1; }
            }
            public class Game
            {
                static Enemy? boss;
                public static int Play()
                {
                    boss = new Enemy();
                    var minion = new Enemy();
                    boss.Hit();
                    minion.Hit();
                    return boss.Hp + minion.Hp;
                }
            }
            """;
        const string probe = """
            local keys = {}
            for k in pairs(__tcs_trace) do keys[#keys + 1] = k end
            table.sort(keys)
            local classes = {}
            for k in pairs(__tcs_trace_classes) do classes[#classes + 1] = k end
            table.sort(classes)
            local n = 0
            collectgarbage()
            for _ in pairs(__tcs_instances) do n = n + 1 end
            print(r, n, table.concat(keys, ","), table.concat(classes, ","))
            """;
        string[] trace = ["game/"];
        var full = Transpiler.TranspileWithDiagnostics([game], ["game/Game.cs"],
            instanceRegistry: true, tracePrefixes: trace);
        Assert.True(full.Success, string.Join("\n", full.Errors));
        var expected = TestHelper.RunLua($"{full.Lua}\nlocal r = Game.Play()\n{probe}").Trim();

        var session = new IncrementalCompilationSession(instanceRegistry: true, tracePrefixes: trace);
        session.OpenProject([("game/Game.cs", game)]);
        var snapshot = ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision, null,
            LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));
        var actual = TestHelper.RunLua($"{TestHelper.LoadChunk(snapshot, "snapshot")}\n"
            + "local r = __tcs_module_runtime.registry.env.Game.Play()\n" + probe).Trim();

        Assert.StartsWith("4\t1\tgame/Game.cs:", expected);
        Assert.Equal(expected, actual);
    }

    // TinySystem.Linq は使うときだけ module になる。本文の編集で初めて使っても
    // library の module を足して全体を emit し直す
    [Fact]
    public void FirstLibraryUseInBodyEdit_AddsLibraryModule()
    {
        const string before = """
            using System.Collections.Generic;
            public static class A
            {
                public static int F() { var l = new List<int> { 1, 2, 3 }; return l.Count; }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", before)]);
        Assert.Equal(["a.cs"], session.Artifacts.Select(a => a.ModuleId));

        var result = session.Update("a.cs", before.Replace("return l.Count;",
            "return TinySystem.Linq.Enumerable.Count(l, x => x > 1);"));

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.False(result.FastPath);
        Assert.Equal(["TinySystem/Linq.cs", "a.cs"], session.Artifacts.Select(a => a.ModuleId));
        var output = StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#A"].F())""");
        Assert.Equal(["2"], output);
    }

    // 手元の版の TinySystem.Linq を後の編集で消すと同梱の library に戻り、足すと同梱の library を外す
    [Fact]
    public void VendoredLibraryAddedOrRemovedInUpdate_SwitchesBundledLibrary()
    {
        const string user = """
            using System.Collections.Generic;
            using TinySystem.Linq;
            public static class A
            {
                public static int F() => new List<int> { 1, 2, 3 }.Count(x => x > 1);
            }
            """;
        const string vendored = """
            using System;
            using System.Collections.Generic;
            namespace TinySystem
            {
                namespace Linq
                {
                    public static class Enumerable
                    {
                        public static int Count<T>(this List<T> source, Func<T, bool> predicate) => 42;
                    }
                }
            }
            """;
        const string other = "public static class B { public static int G() => 1; }";
        var session = new IncrementalCompilationSession();
        session.OpenProject([("a.cs", user), ("v.cs", vendored)]);
        Assert.Equal(["a.cs", "v.cs"], session.Artifacts.Select(a => a.ModuleId));
        Assert.Equal(["42"], StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#A"].F())"""));

        var removed = session.Update("v.cs", other);
        Assert.True(removed.Success, string.Join("\n", removed.Errors));
        Assert.Equal(["TinySystem/Linq.cs", "a.cs", "v.cs"], session.Artifacts.Select(a => a.ModuleId));
        Assert.Equal(["2"], StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#A"].F())"""));

        var added = session.Update("v.cs", vendored);
        Assert.True(added.Success, string.Join("\n", added.Errors));
        Assert.Equal(["a.cs", "v.cs"], session.Artifacts.Select(a => a.ModuleId));
        Assert.Equal(["42"], StaticInitOrderTests.RunSnapshot(StaticInitOrderTests.Snapshot(session),
            """print(reg.types["a.cs#A"].F())"""));
    }

    // 同梱の library の path は、手元の版でない user の module には使えない
    [Fact]
    public void UserModuleAtLibraryPath_IsRejected()
    {
        var session = new IncrementalCompilationSession();
        var ex = Assert.Throws<ArgumentException>(() =>
            session.OpenProject([("TinySystem/Linq.cs", "public static class Helper { }")]));
        Assert.Contains("TinySystem/Linq.cs", ex.Message);
    }

    [Fact]
    public void SignatureEdit_TakesSlowPath_ReemitsAllModules()
    {
        var session = Open();
        // parameter 名変更は body 外 → 無条件 slow path (§8.1)
        var result = session.Update("game/Counter.cs",
            FileA.Replace("Add(int amount)", "Add(int delta)")
                 .Replace("Value + amount", "Value + delta"));
        Assert.True(result.Success);
        Assert.False(result.FastPath);
        Assert.Equal(2, result.EmittedModuleCount);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void CommentOnlyEditOutsideBody_TakesSlowPath()
    {
        var session = Open();
        // 宣言部のコメント追加: surface hash は不変 (trivia 除外) だが
        // span が body 外 → fail-safe で slow path (§8.1)
        var result = session.Update("game/Counter.cs",
            FileA.Replace("public int Value;", "public int Value; // hp"));
        Assert.True(result.Success);
        Assert.False(result.FastPath);
    }

    // build を止める TCS1001 (NamedArgument) は session でも error 扱い:
    // open 時は emit せず、edit 時は last-good を保って commit しない (#19)
    [Fact]
    public void BuildBlockingDiagnostic_BlocksOpenAndUpdate()
    {
        const string named = """
            public class Counter
            {
                public int Value;
                public void Add(int amount) { Value = Value + amount; }
                public static int Magic() { return Pick(y: 1, x: 2); }
                public static int Pick(int x, int y) { return x; }
            }
            """;
        var blocked = new IncrementalCompilationSession();
        blocked.OpenProject([("game/Counter.cs", named), ("game/Game.cs", FileB)]);
        var (errors, warnings) = blocked.CollectDiagnostics();
        Assert.Contains(errors, e => e.Contains("error TCS1001")
            && e.Contains("NamedArgument"));
        Assert.DoesNotContain(warnings, w => w.Contains("NamedArgument"));
        Assert.Equal(0, blocked.Revision);
        Assert.Empty(blocked.Artifacts);
        AssertDiagnosticsParity(blocked);

        var session = Open();
        var before = session.Revision;
        var result = session.Update("game/Counter.cs", named);
        Assert.False(result.Success);
        Assert.Contains(result.Errors, e => e.Contains("NamedArgument"));
        Assert.Empty(result.ChangedArtifacts);
        Assert.Equal(before, session.Revision);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);

        var recovered = session.Update("game/Counter.cs", FileA);
        Assert.True(recovered.Success);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
    }

    // emit して初めて見つかる未対応 (sizeof は IL にできず emitter が出す
    // TCS1001) も error: open は commit せず、edit は fast / slow path とも
    // last-good を保つ
    [Fact]
    public void EmitterFoundUnsupported_BlocksOpenAndUpdate()
    {
        var bodyEdit = FileA.Replace("return 10;", "return sizeof(int) * 10;");
        var blocked = new IncrementalCompilationSession();
        blocked.OpenProject([("game/Counter.cs", bodyEdit), ("game/Game.cs", FileB)]);
        var (errors, _) = blocked.CollectDiagnostics();
        Assert.Contains(errors, e => e.Contains("error TCS1001")
            && e.Contains("SizeOfExpression"));
        Assert.Equal(0, blocked.Revision);
        Assert.Empty(blocked.Artifacts);
        AssertDiagnosticsParity(blocked);

        var session = Open();
        var before = session.Revision;
        var fast = session.Update("game/Counter.cs", bodyEdit);
        Assert.True(fast.FastPath);
        Assert.False(fast.Success);
        Assert.Contains(fast.Errors, e => e.Contains("SizeOfExpression"));
        Assert.Empty(fast.ChangedArtifacts);
        Assert.Equal(before, session.Revision);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);

        Assert.True(session.Update("game/Counter.cs", FileA).Success);
        before = session.Revision;
        var slow = session.Update("game/Counter.cs", FileA.Replace(
            "public int Value;", "public int Value; public static int H = sizeof(int) * 3;"));
        Assert.False(slow.FastPath);
        Assert.False(slow.Success);
        Assert.Contains(slow.Errors, e => e.Contains("SizeOfExpression"));
        Assert.Equal(before, session.Revision);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void ErrorEdit_KeepsLastGoodArtifacts()
    {
        var session = Open();
        var before = session.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua;
        var result = session.Update("game/Counter.cs",
            FileA.Replace("return 10;", "return Undefined;"));
        Assert.False(result.Success);
        Assert.NotEmpty(result.Errors);
        Assert.Empty(result.ChangedArtifacts);
        // last-good は維持され、リンク済み出力は編集前のまま動く (§7.3)
        Assert.Equal(before,
            session.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua);
        Assert.Equal("10", LinkAndRun(session, "Game.Play()"));
    }

    [Fact]
    public void CrossFileErrorRecovery_ReemitsModuleEditedDuringError()
    {
        var session = Open();
        // A を壊す
        var broken = session.Update("game/Counter.cs",
            FileA.Replace("return 10;", "return Undefined;"));
        Assert.False(broken.Success);
        // error 中に B を編集 (artifact は publish されない)
        var editedB = FileB.Replace("return c.Value;", "return c.Value + 100;");
        var whileBroken = session.Update("game/Game.cs", editedB);
        Assert.False(whileBroken.Success);
        // A を修正して復帰 → B も含めて再 emit される (dirty closure、§7.3/§18.1)
        var recovered = session.Update("game/Counter.cs",
            FileA.Replace("return 10;", "return 1;"));
        Assert.True(recovered.Success);
        Assert.Contains(recovered.ChangedArtifacts, a => a.ModuleId == "game/Game.cs");
        Assert.Equal("101", LinkAndRun(session, "Game.Play()"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void SessionDiagnostics_MatchFullBuild_WithWarnings()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([
            ("game/T.cs", """
                public class T
                {
                    public static int Test() { var names = new string[2]; return names.Length; }
                }
                """),
        ]);
        Assert.Contains(session.CollectDiagnostics().Warnings, w => w.Contains("TCS1003"));
        AssertDiagnosticsParity(session);
        // body edit 後も parity が保たれる
        var result = session.Update("game/T.cs", """
            public class T
            {
                public static int Test() { var names = new string[3]; return names.Length; }
            }
            """);
        Assert.True(result.Success);
        Assert.True(result.FastPath);
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void SessionDiagnostics_LuaKeywordMemberIsError()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("game/Counter.cs", FileA), ("game/Game.cs", FileB)]);
        Assert.Empty(session.CollectDiagnostics().Errors);
        // member 追加は slow path。予約語の member は error で emit を止める
        var result = session.Update("game/Counter.cs",
            FileA.Replace("public int Value;", "public int Value; public int end_; public void end() { }"));
        Assert.False(result.Success);
        Assert.False(result.FastPath);
        var error = Assert.Single(result.Errors);
        Assert.Contains("error TCS1001: unsupported syntax: LuaKeywordIdentifier(end)", error);
        Assert.Contains(session.CollectDiagnostics().Errors,
            e => e.Contains("LuaKeywordIdentifier(end)"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void BodyEdit_SplicedEmit_MatchesFullEmitBytes()
    {
        var session = Open();
        // 1回目: Magic の body 編集 (splice)
        var edited1 = FileA.Replace("return 10;", "return 42;");
        session.Update("game/Counter.cs", edited1);
        // 2回目: 別 method (Add) の body 編集 — range shift 後の splice を検証
        var edited2 = edited1.Replace("Value = Value + amount;",
            "Value = Value + amount + 1;");
        session.Update("game/Counter.cs", edited2);
        var spliced = session.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua;

        var fresh = new IncrementalCompilationSession();
        fresh.OpenProject([("game/Counter.cs", edited2), ("game/Game.cs", FileB)]);
        var full = fresh.Artifacts.Single(a => a.ModuleId == "game/Counter.cs").Lua;
        Assert.Equal(full, spliced);
        Assert.Equal("43", LinkAndRun(session, "Game.Play()"));
    }

    [Fact]
    public void MinimalChange_FindsInnerSpan()
    {
        var change = IncrementalCompilationSession.MinimalChange(
            "aaa BBB ccc", "aaa XY ccc");
        Assert.Equal(4, change.Span.Start);
        Assert.Equal(3, change.Span.Length);
        Assert.Equal("XY", change.NewText);
    }

    [Fact]
    public void SurfaceHash_IgnoresBodyAndTrivia_DetectsSignature()
    {
        var t1 = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(FileA);
        var bodyEdit = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            FileA.Replace("return 10;", "return 99;"));
        var commented = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            FileA.Replace("public int Value;", "public int Value; // hp"));
        var paramRenamed = Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree.ParseText(
            FileA.Replace("Add(int amount)", "Add(int delta)"));
        var h1 = IncrementalCompilationSession.SurfaceHash(t1);
        Assert.Equal(h1, IncrementalCompilationSession.SurfaceHash(bodyEdit));
        Assert.Equal(h1, IncrementalCompilationSession.SurfaceHash(commented));
        Assert.NotEqual(h1, IncrementalCompilationSession.SurfaceHash(paramRenamed));
    }

    // 固定した module (編集しない前提の module) を除いて、診断と emit を編集した側だけにする
    private const string FixedLib = """
        public class Lib
        {
            public static int Base() { return 100; }
        }
        """;

    private const string EditableA = """
        public class A
        {
            public static int Tuning = 1;
            public static int Get() { return Lib.Base() + Tuning; }
        }
        """;

    private const string EditableB = """
        public class B
        {
            public static int Play() { return A.Get(); }
        }
        """;

    private static IncrementalCompilationSession OpenFixed()
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject(
            [("lib/Lib.cs", FixedLib), ("game/A.cs", EditableA), ("game/B.cs", EditableB)],
            ["lib/Lib.cs"]);
        return session;
    }

    [Fact]
    public void Fixed_declaration_edit_emits_only_the_editable_modules()
    {
        var session = OpenFixed();
        var r = session.Update("game/A.cs", EditableA.Replace("Tuning = 1", "Tuning = 2"));
        Assert.True(r.Success);
        Assert.False(r.FastPath);
        Assert.Equal(["game/A.cs", "game/B.cs"], r.ChangedArtifacts.Select(a => a.ModuleId));
        Assert.Equal(["lib/Lib.cs", "game/A.cs", "game/B.cs"], session.Artifacts.Select(a => a.ModuleId));
        Assert.Equal("102", LinkAndRun(session, "B.Play()"));
    }

    [Fact]
    public void Fixed_output_matches_a_session_without_fixed_modules()
    {
        var plain = new IncrementalCompilationSession();
        plain.OpenProject([("lib/Lib.cs", FixedLib), ("game/A.cs", EditableA), ("game/B.cs", EditableB)]);
        var session = OpenFixed();
        var edited = EditableA.Replace("Tuning = 1", "Tuning = 2") + "\npublic class C { }\n";
        plain.Update("game/A.cs", edited);
        session.Update("game/A.cs", edited);
        Assert.Equal(plain.Artifacts.Select(a => a.Lua), session.Artifacts.Select(a => a.Lua));
    }

    [Fact]
    public void Fixed_still_reports_errors_across_editable_modules()
    {
        var session = OpenFixed();
        var r = session.Update("game/A.cs", EditableA.Replace("Get()", "Renamed()"));
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("game/B.cs") && e.Contains("CS0117"));
        AssertDiagnosticsParity(session);
    }

    [Fact]
    public void Fixed_reports_a_type_that_collides_with_a_fixed_module()
    {
        var session = OpenFixed();
        var r = session.Update("game/A.cs", EditableA + "\npublic class Lib { }\n");
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("CS0101"));
    }

    [Fact]
    public void Fixed_module_edit_checks_and_emits_everything()
    {
        var session = OpenFixed();
        var r = session.Update("lib/Lib.cs", FixedLib + "\npublic class Extra { }\n");
        Assert.True(r.Success);
        Assert.Equal(3, r.EmittedModuleCount);
    }

    [Fact]
    public void Fixed_module_error_is_not_hidden_by_a_later_editable_edit()
    {
        var session = OpenFixed();
        Assert.False(session.Update("lib/Lib.cs", FixedLib.Replace("Base()", "Gone()")).Success);
        var r = session.Update("game/A.cs", EditableA.Replace("Tuning = 1", "Tuning = 2"));
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("CS0117"));
        r = session.Update("lib/Lib.cs", FixedLib);
        Assert.True(r.Success);
        Assert.Equal(3, r.EmittedModuleCount);
        Assert.Equal("102", LinkAndRun(session, "B.Play()"));
    }

    [Fact]
    public void Fixed_module_body_error_is_not_hidden_by_a_later_editable_edit()
    {
        var session = OpenFixed();
        Assert.False(session.Update("lib/Lib.cs", FixedLib.Replace("return 100;", "return nope;")).Success);
        var r = session.Update("game/A.cs", EditableA.Replace("Tuning = 1", "Tuning = 2"));
        Assert.False(r.Success);
        Assert.Contains(r.Errors, e => e.Contains("CS0103"));
    }

    [Fact]
    public void Fixed_migration_matches_a_session_without_fixed_modules()
    {
        const string lib = "public struct P { public int X; }\n" + FixedLib;
        const string game = """
            public class Owner
            {
                public P Pos;
                public static Owner One = new Owner();
                public static int Get() { return Lib.Base(); }
            }
            """;
        var edited = game.Replace("public P Pos;", "public P Pos;\n    public int Added;");
        (string, string)[] files = [("lib/Lib.cs", lib), ("game/Owner.cs", game)];
        var plain = new IncrementalCompilationSession();
        plain.OpenProject(files);
        var session = new IncrementalCompilationSession();
        session.OpenProject(files, ["lib/Lib.cs"]);
        Assert.True(plain.Update("game/Owner.cs", edited).Success);
        Assert.True(session.Update("game/Owner.cs", edited).Success);
        var migration = session.MigrationFrom(session.Revision - 1);
        Assert.NotNull(migration);
        Assert.Equal(plain.MigrationFrom(plain.Revision - 1), migration);
    }

    [Fact]
    public void Fixed_module_shape_change_reaches_the_migration()
    {
        const string lib = "public class Keep { public int X; }\n" + FixedLib;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("lib/Lib.cs", lib), ("game/A.cs", EditableA), ("game/B.cs", EditableB)], ["lib/Lib.cs"]);
        Assert.True(session.Update("lib/Lib.cs", lib.Replace("public int X;", "public int X;\n    public int Y;")).Success);
        Assert.NotNull(session.MigrationFrom(session.Revision - 1));
        Assert.True(session.Update("game/A.cs", EditableA.Replace("Tuning = 1", "Tuning = 2")).Success);
        Assert.Null(session.MigrationFrom(session.Revision - 1));
    }
}
