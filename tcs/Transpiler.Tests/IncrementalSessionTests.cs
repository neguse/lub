namespace TinyCs.Tests;

// T175: IncrementalCompilationSession の M1 core 検証
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
        var session = new IncrementalCompilationSession(checkNaming: false);
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
        var session = new IncrementalCompilationSession(checkNaming: true);
        session.OpenProject([
            ("game/T.cs", """
                public class T
                {
                    public static int test() { return 1; }
                }
                """),
        ]);
        AssertDiagnosticsParity(session);
        // body edit 後も parity が保たれる
        var result = session.Update("game/T.cs", """
            public class T
            {
                public static int test() { return 2; }
            }
            """);
        Assert.True(result.Success);
        Assert.True(result.FastPath);
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

        var fresh = new IncrementalCompilationSession(checkNaming: false);
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
