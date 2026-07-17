namespace TinyCs.Tests;

// T230: SessionExports 向け補完/hover クエリ (SemanticQueries) の検証。
// - speculative fork (ForkWithContent) が session 状態を変えないこと
// - allowlist フィルタ (「補完に出る = tcs で書ける」) が効くこと
public class SemanticQueriesTests
{
    private const string GameSource = """
        public class Player
        {
            /// <summary>プレイヤーの体力。</summary>
            public int Health;
            public void Damage(int amount) { Health = Health - amount; }
            public static Player Spawn() { return new Player(); }
        }
        """;

    private static IncrementalCompilationSession Open(string source)
    {
        var session = new IncrementalCompilationSession(checkNaming: false);
        session.OpenProject([("game/Player.cs", source)]);
        return session;
    }

    private static List<CompletionItem> CompleteAt(
        IncrementalCompilationSession session, string path, string content,
        string marker)
    {
        var offset = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"marker not found: {marker}");
        var fork = session.ForkWithContent(path, content);
        Assert.NotNull(fork);
        return SemanticQueries.Complete(
            fork.Value.Compilation, fork.Value.Tree, offset + marker.Length);
    }

    // ------------------------------------------------------------------
    [Fact]
    public void InstanceMemberCompletionAfterDot()
    {
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "Health = Health - amount; this.");
        var items = CompleteAt(session, "game/Player.cs", content,
            "amount; this.");
        Assert.Contains(items, i => i.Label == "Damage" && i.Kind == "method");
        Assert.Contains(items, i => i.Label == "Health" && i.Kind == "variable");
        // static は instance receiver に出ない
        Assert.DoesNotContain(items, i => i.Label == "Spawn");
    }

    [Fact]
    public void StaticMemberCompletionOnTypeReceiver()
    {
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "Health = Health - amount; var p = Player.");
        var items = CompleteAt(session, "game/Player.cs", content, "= Player.");
        Assert.Contains(items, i => i.Label == "Spawn" && i.Kind == "method");
        Assert.DoesNotContain(items, i => i.Label == "Damage");
    }

    [Fact]
    public void AllowlistFiltersUnsupportedBclMembers()
    {
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "Health = Health - amount; var x = System.Math.");
        var items = CompleteAt(session, "game/Player.cs", content,
            "= System.Math.");
        // allowlist 内は出る
        Assert.Contains(items, i => i.Label == "Min");
        Assert.Contains(items, i => i.Label == "PI");
        // allowlist 外 (TCS1002 対象) は補完に出さない
        Assert.DoesNotContain(items, i => i.Label == "Cbrt");
        Assert.DoesNotContain(items, i => i.Label == "BigMul");
    }

    [Fact]
    public void ListCompletionFiltersUnsupportedMembers()
    {
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "Health = Health - amount; var l = new System.Collections.Generic.List<int>(); l.");
        var items = CompleteAt(session, "game/Player.cs", content, "(); l.");
        Assert.Contains(items, i => i.Label == "Add");
        Assert.Contains(items, i => i.Label == "Count");
        // List.Reverse は allowlist 外
        Assert.DoesNotContain(items, i => i.Label == "Reverse");
    }

    [Fact]
    public void ScopeCompletionSeesLocalsAndParameters()
    {
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "var remaining = Health - amount; ");
        var offset = content.IndexOf("amount; ", StringComparison.Ordinal)
            + "amount; ".Length;
        var fork = session.ForkWithContent("game/Player.cs", content);
        Assert.NotNull(fork);
        var items = SemanticQueries.Complete(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.Contains(items, i => i.Label == "remaining" && i.Kind == "variable");
        Assert.Contains(items, i => i.Label == "amount" && i.Kind == "variable");
        Assert.Contains(items, i => i.Label == "Player" && i.Kind == "class");
    }

    [Fact]
    public void HoverShowsSignatureAndDocSummary()
    {
        var session = Open(GameSource);
        var fork = session.ForkWithContent("game/Player.cs", GameSource);
        Assert.NotNull(fork);
        // `Health = Health - amount` の参照側 Health に hover
        var offset = GameSource.IndexOf("Health - amount",
            StringComparison.Ordinal);
        var info = SemanticQueries.Hover(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.NotNull(info);
        Assert.Contains("Health", info.Display);
        Assert.Contains("int", info.Display);
        Assert.Equal("プレイヤーの体力。", info.Doc);
        Assert.Equal(offset, info.Start);
        Assert.Equal(offset + "Health".Length, info.End);
    }

    [Fact]
    public void HoverOnUnresolvedIdentifierReturnsNull()
    {
        // 未解決識別子で親式 (BinaryExpression) の operator symbol 等を
        // 拾わないこと (PR #1 レビュー指摘)
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;", "Health = undefinedA + 1;");
        var fork = session.ForkWithContent("game/Player.cs", content);
        Assert.NotNull(fork);
        var offset = content.IndexOf("undefinedA", StringComparison.Ordinal) + 2;
        var info = SemanticQueries.Hover(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.Null(info);
    }

    [Fact]
    public void HoverOnDeclarationShowsSymbol()
    {
        var session = Open(GameSource);
        var fork = session.ForkWithContent("game/Player.cs", GameSource);
        Assert.NotNull(fork);
        // メソッド宣言の識別子 (walk なしで GetDeclaredSymbol が引けること)
        var offset = GameSource.IndexOf("Damage", StringComparison.Ordinal) + 1;
        var info = SemanticQueries.Hover(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.NotNull(info);
        Assert.Contains("Damage", info.Display);
    }

    [Fact]
    public void HoverOnNonIdentifierReturnsNull()
    {
        var session = Open(GameSource);
        var fork = session.ForkWithContent("game/Player.cs", GameSource);
        Assert.NotNull(fork);
        var offset = GameSource.IndexOf(" - amount", StringComparison.Ordinal)
            + 1; // `-` 演算子
        var info = SemanticQueries.Hover(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.Null(info);
    }

    [Fact]
    public void ForkDoesNotMutateSessionState()
    {
        var session = Open(GameSource);
        var revBefore = session.Revision;
        var luaBefore = string.Join("", session.Artifacts.Select(a => a.Lua));

        // 壊れた speculative 内容でクエリしても session は不変
        var broken = GameSource.Replace(
            "Health = Health - amount;", "Health = thisIsUndefined.");
        var items = CompleteAt(session, "game/Player.cs", broken,
            "thisIsUndefined.");
        _ = items; // 空でもよい (バインド不能 receiver)

        Assert.Equal(revBefore, session.Revision);
        Assert.Equal(luaBefore,
            string.Join("", session.Artifacts.Select(a => a.Lua)));

        // fork 後も通常の Update が正しく動く
        var r = session.Update("game/Player.cs", GameSource.Replace(
            "Health = Health - amount;", "Health = Health - amount * 2;"));
        Assert.True(r.Success);
    }

    [Fact]
    public void SpeculativeContentIsQueryable()
    {
        // session が知らない編集途中の内容 (Update 前) でも補完できる
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "var self = Spawn(); self.");
        var items = CompleteAt(session, "game/Player.cs", content, "self.");
        Assert.Contains(items, i => i.Label == "Damage");
    }
}
