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

    // 書き換えない parameter の copy は呼び出し側で省くが、書き換えるかは呼ばれた側の本文で決まるので、
    // 呼ばれた側の本文だけの編集 (呼び出し側は emit し直さない) で意味が変わらない
    [Fact]
    public void BodyEdit_ThatStartsWritingStructParameter_KeepsCallerValue()
    {
        const string callee = """
            public struct V { public int X; }
            public static class M
            {
                public static int Read(V v)
                {
                    return v.X;
                }
            }
            """;
        const string caller = """
            public static class Game
            {
                public static int Play()
                {
                    var a = new V();
                    a.X = 3;
                    return M.Read(a) * 10 + a.X;
                }
            }
            """;
        var session = new IncrementalCompilationSession();
        session.OpenProject([("m.cs", callee), ("game.cs", caller)]);
        Assert.Equal("33", LinkAndRun(session, "Game.Play()"));

        var result = session.Update("m.cs", callee.Replace("return v.X;", "v.X = 9; return v.X;"));

        Assert.True(result.FastPath);
        Assert.Equal("m.cs", result.ChangedArtifacts.Single().ModuleId);
        Assert.Equal("93", LinkAndRun(session, "Game.Play()"));
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
}
