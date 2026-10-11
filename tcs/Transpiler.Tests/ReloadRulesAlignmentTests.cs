namespace TinyCs.Tests;

// reload chunk (tcs --watch --reload-chunks、ReloadClassifier) と module registry
// (web の増分 session、IncrementalCompilationSession) は、同じ編集を同じ規則で
// restart / live に分ける (doc/hot-reload-design.md §11 の規則表)
public class ReloadRulesAlignmentTests
{
    private const string Game = """
        public enum E { One, Two }
        public class G
        {
            public const int K = 1;
            public static int X = 1;
            public static int Y = 1 + 2;
            public static int Z = G.Make( 1 );
            public static int Make(int v) { return v; }
            public static float F = 1f / 3f;
            public int A = 1 + 2;
            public string S;
        }
        public record R(int X);
        """;

    // 両方で restart にする編集と、その理由
    [Theory]
    [InlineData("const int K = 1;", "const int K = 2;", "static initializer changed: G.k")]
    [InlineData("static int X = 1;", "static int X = 2;", "static initializer changed: G.x")]
    [InlineData("static int X = 1;", "static float X = 2f;", "static type changed: G.x")]
    [InlineData("static int X = 1;", "static float X = 1;", "static type changed: G.x")]
    [InlineData("enum E { One, Two }", "enum E { Two, One }",
        "enum member removed or renumbered: E.ONE")]
    [InlineData("enum E { One, Two }", "enum E { One }",
        "enum member removed or renumbered: E.TWO")]
    [InlineData("enum E { One, Two }", "enum E { One, Two = 5 }",
        "enum member removed or renumbered: E.TWO")]
    [InlineData("F = 1f / 3f;", "F = 0.333333343f;", "static initializer changed: G.f")]
    // const は使う側に埋め込まれ、static の table の値は戻らない (実行中に変えた値が残る)
    [InlineData("public const int K = 1;", "public static int K = 1;",
        "static initializer changed: G.k")]
    [InlineData("public static int X = 1;", "public const int X = 1;",
        "static initializer changed: G.x")]
    public void RestartEdit_RestartsInBoth(string from, string to, string reason)
    {
        var edited = Edit(from, to);

        Assert.Contains(reason, Native(edited));
        Assert.Contains(Session(edited), r => r.EndsWith(": " + reason, StringComparison.Ordinal));
    }

    // 両方で live にする編集。initializer は空白・comment・定数の書き方では変わらない
    [Theory]
    [InlineData("1 + 2;\n    public static int Z", "1+2;\n    public static int Z")]
    [InlineData("1 + 2;\n    public static int Z", "3;\n    public static int Z")]
    [InlineData("G.Make( 1 )", "G.Make(1 /* seed */)")]
    [InlineData("public int A = 1 + 2;", "public int A = 1+2;")]
    [InlineData("enum E { One, Two }", "enum E { One, Two, Three }")]
    [InlineData("F = 1f / 3f;", "F = 1f/3f;")]
    public void LiveEdit_IsLiveInBoth(string from, string to)
    {
        var edited = Edit(from, to);

        Assert.Empty(Native(edited));
        Assert.Empty(Session(edited));
    }

    // 意図した差: reload chunk は instance を registry 経由で移行し、新しい static の
    // initializer を chunk の中で評価する。module registry はどちらも restart にする
    [Theory]
    [InlineData("public int A = 1 + 2;", "public int A = 1 + 2;\n    public int B = 4;",
        "instance shape changed: G")]
    [InlineData("public int A = 1 + 2;", "public float A = 1 + 2;", "instance shape changed: G")]
    [InlineData("public string S;", "public int[] S;", "instance shape changed: G")]
    [InlineData("record R(int X)", "record R(float X)", "instance shape changed: R")]
    [InlineData("public static int X = 1;",
        "public static int X = 1;\n    public static int W = G.Make(2);",
        "impure new static: G.w")]
    public void InstanceAndImpureStaticEdit_IsLiveOnlyInReloadChunk(
        string from, string to, string reason)
    {
        var edited = Edit(from, to);

        Assert.Empty(Native(edited));
        Assert.Contains(Session(edited), r => r.EndsWith(": " + reason, StringComparison.Ordinal));
    }

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Game);
        return Game.Replace(from, to);
    }

    private static IReadOnlyList<string> Native(string edited) =>
        HotReload.PlanReload([Game], [edited]).RestartReasons;

    private static List<string> Session(string edited)
    {
        var session = new IncrementalCompilationSession(checkNaming: false);
        session.OpenProject([("game.cs", Game)]);
        var result = session.Update("game.cs", edited);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(result.RestartReasons.Count > 0, result.RequiresRestart);
        return result.RestartReasons;
    }
}
