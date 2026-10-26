namespace TinyCs.Tests;

// session (IncrementalCompilationSession、watch と web の両方) が編集を restart / live に
// 分ける規則 (doc/hot-reload-design.md §11 の規則表)
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
            public int Use(int v) { return v; }
            public int Twice(int v) { return Use(v) * 2; }
        }
        public record R(int X);
        """;

    // method の改名 (呼び出し側も同じ build で作り直す) は live。名前は C# の
    // 識別子のままなので、改名は旧 key の削除と新 key の追加になる。overload に
    // 引数の型を付ける名前 (q.md Q16) も同じ経路を通る
    [Fact]
    public void MethodRename_WithCallersRebuilt_IsLive()
    {
        var edited = Game.Replace("Use(", "Fetch(");
        Assert.NotEqual(Game, edited);

        Assert.Empty(Session(edited));
    }

    // static method に overload を足すと既存の名前に引数の型が付く (`Make` →
    // `Make__int`)。呼び出し側も同じ build で作り直すので、改名と同じく live
    [Fact]
    public void AddingStaticOverload_IsLive()
    {
        var edited = Edit("public static int Make(int v) { return v; }",
            "public static int Make(int v) { return v; }\n" +
            "    public static int Make(int v, int w) { return v + w; }");

        Assert.Empty(Session(edited));
    }

    // instance method の overload も同じ (`Use` → `Use__int`、呼び出し側も作り直す)
    [Fact]
    public void AddingInstanceOverload_IsLive()
    {
        var edited = Edit("public int Use(int v) { return v; }",
            "public int Use(int v) { return v; }\n" +
            "    public int Use(int v, int w) { return v + w; }");

        Assert.Empty(Session(edited));
    }

    // restart にする編集と、その理由
    [Theory]
    [InlineData("const int K = 1;", "const int K = 2;", "static initializer changed: G.K")]
    [InlineData("static int X = 1;", "static int X = 2;", "static initializer changed: G.X")]
    [InlineData("static int X = 1;", "static float X = 2f;", "static type changed: G.X")]
    [InlineData("static int X = 1;", "static float X = 1;", "static type changed: G.X")]
    [InlineData("enum E { One, Two }", "enum E { Two, One }",
        "enum member removed or renumbered: E.One")]
    [InlineData("enum E { One, Two }", "enum E { One }",
        "enum member removed or renumbered: E.Two")]
    [InlineData("enum E { One, Two }", "enum E { One, Two = 5 }",
        "enum member removed or renumbered: E.Two")]
    [InlineData("F = 1f / 3f;", "F = 0.333333343f;", "static initializer changed: G.F")]
    // const は使う側に埋め込まれ、static の table の値は戻らない (実行中に変えた値が残る)
    [InlineData("public const int K = 1;", "public static int K = 1;",
        "static initializer changed: G.K")]
    [InlineData("public static int X = 1;", "public const int X = 1;",
        "static initializer changed: G.X")]
    public void RestartEdit_Restarts(string from, string to, string reason)
    {
        var edited = Edit(from, to);

        Assert.Contains(Session(edited), r => r.EndsWith(": " + reason, StringComparison.Ordinal));
    }

    // live にする編集。initializer は空白・comment・定数の書き方では変わらない
    [Theory]
    [InlineData("1 + 2;\n    public static int Z", "1+2;\n    public static int Z")]
    [InlineData("1 + 2;\n    public static int Z", "3;\n    public static int Z")]
    [InlineData("G.Make( 1 )", "G.Make(1 /* seed */)")]
    [InlineData("public int A = 1 + 2;", "public int A = 1+2;")]
    [InlineData("enum E { One, Two }", "enum E { One, Two, Three }")]
    [InlineData("F = 1f / 3f;", "F = 1f/3f;")]
    // instance を registry 経由で移行し、新しい static の initializer を当てる時に評価する
    [InlineData("public int A = 1 + 2;", "public int A = 1 + 2;\n    public int B = 4;")]
    [InlineData("public int A = 1 + 2;", "public float A = 1 + 2;")]
    [InlineData("public string S;", "public int[] S;")]
    [InlineData("record R(int X)", "record R(float X)")]
    [InlineData("public static int X = 1;",
        "public static int X = 1;\n    public static int W = G.Make(2);")]
    public void LiveEdit_IsLive(string from, string to)
    {
        var edited = Edit(from, to);

        Assert.Empty(Session(edited));
    }

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Game);
        return Game.Replace(from, to);
    }

    private static List<string> Session(string edited)
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject([("game.cs", Game)]);
        var result = session.Update("game.cs", edited);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Equal(result.RestartReasons.Count > 0, result.RequiresRestart);
        return result.RestartReasons;
    }
}
