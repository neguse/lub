namespace TinyCs.Tests;

// Tests of the completion/hover queries (SemanticQueries) for SessionExports.
// - the speculative fork (ForkWithContent) does not change the session's text
// - the allowlist filter ("shown in completion = writable in tcs") works
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

    private static EditorSession Open(string source) => new([("game/Player.cs", source)]);

    private static List<CompletionItem> CompleteAt(
        EditorSession session, string path, string content,
        string marker)
    {
        var offset = content.IndexOf(marker, StringComparison.Ordinal);
        Assert.True(offset >= 0, $"marker not found: {marker}");
        var fork = session.ForkWithContent(path, content);
        Assert.NotNull(fork);
        return SemanticQueries.Complete(
            fork.Value.Compilation, fork.Value.Tree, offset + marker.Length);
    }

    // A path not in the session cannot be forked. The text of other files is kept current with SetText.
    [Fact]
    public void EditorSession_KnowsOnlyItsFiles()
    {
        var editor = Open(GameSource);
        Assert.Null(editor.ForkWithContent("game/Other.cs", GameSource));
        Assert.True(editor.SetText("game/Player.cs", GameSource));
        Assert.False(editor.SetText("game/Other.cs", GameSource));
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
        // Statics do not appear on an instance receiver
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
        // Members in the allowlist appear
        Assert.Contains(items, i => i.Label == "Min");
        Assert.Contains(items, i => i.Label == "PI");
        // Members outside the allowlist (subject to TCS1002) are not shown in completion
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
        // List.Reverse is outside the allowlist
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
        // Hover over the referencing Health in `Health = Health - amount`
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
        // On an unresolved identifier, do not pick up the parent expression's (BinaryExpression)
        // operator symbol etc. (PR #1 review comment)
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
        // Identifier of a method declaration (GetDeclaredSymbol resolves it without a walk)
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
            + 1; // `-` operator
        var info = SemanticQueries.Hover(
            fork.Value.Compilation, fork.Value.Tree, offset);
        Assert.Null(info);
    }

    [Fact]
    public void ForkDoesNotMutateSessionState()
    {
        const string gameFile = "public static class Game { public static int Hp() { return Player.Spawn().Health; } }";
        var session = new EditorSession([("game/Player.cs", GameSource), ("game/Game.cs", gameFile)]);

        // Querying with broken speculative content does not change the session's text
        var broken = GameSource.Replace(
            "Health = Health - amount;", "Health = thisIsUndefined.");
        var items = CompleteAt(session, "game/Player.cs", broken,
            "thisIsUndefined.");
        _ = items; // May be empty (receiver cannot be bound)

        // A fork of another file uses the original text of Player.cs
        var other = session.ForkWithContent("game/Game.cs", gameFile)!.Value;
        var player = other.Compilation.SyntaxTrees.Single(t => t.FilePath == "game/Player.cs");
        Assert.Equal(GameSource, player.ToString());
        Assert.DoesNotContain(other.Compilation.GetDiagnostics(), d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [Fact]
    public void SpeculativeContentIsQueryable()
    {
        // Completion works even with in-progress content the session does not know about (before Update)
        var session = Open(GameSource);
        var content = GameSource.Replace(
            "Health = Health - amount;",
            "var self = Spawn(); self.");
        var items = CompleteAt(session, "game/Player.cs", content, "self.");
        Assert.Contains(items, i => i.Label == "Damage");
    }
}
