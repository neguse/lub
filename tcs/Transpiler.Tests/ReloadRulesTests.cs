namespace TinyCs.Tests;

// hot reload で live に当てられない編集 (doc/hot-reload-design.md §11 の規則表、SelfHost/Tool/ReloadRules.cs)。
// tcs VM の reload (tcs vm --reload-from、--watch、VmSession) が IL の型から同じ規則で restart / live に分ける。
// live の編集の実際の移行は tcs2c.Tests の VmReloadTests と VmReloadCaseTests
public class ReloadRulesTests
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

    private static List<string> RestartReasons(string v1, string v2)
    {
        var old = IlExport.Export([v1]);
        var next = IlExport.Export([v2]);
        Assert.Empty(old.Diagnostics);
        Assert.Empty(next.Diagnostics);
        return TcsSelf.ReloadRules.RestartReasons(TcsSelf.ReloadRules.TypesOf(old), TcsSelf.ReloadRules.TypesOf(next));
    }

    private static string Edit(string from, string to)
    {
        Assert.Contains(from, Game);
        return Game.Replace(from, to);
    }

    [Theory]
    [InlineData(
        "public class G { public static int X = 1; }",
        "public class G { public static int X = 2; }",
        "static initializer changed: G.X")]
    [InlineData(
        "public class G { public static int X; }",
        "public class G { public static int X = 2; }",
        "static initializer changed: G.X")]
    [InlineData(
        "public class G { public static int P { get; set; } = 1; }",
        "public class G { public static int P { get; set; } = 2; }",
        "static initializer changed: G.P")]
    [InlineData( // const は使う側に埋め込まれるが、static の initializer は再評価されない
        "public class G { public const int K = 1; public static int X = K * 2; }",
        "public class G { public const int K = 2; public static int X = K * 2; }",
        "static initializer changed: G.K")]
    [InlineData( // 保持した値は旧い型のまま
        "public class G { public static int X = 1; }",
        "public class G { public static float X = 1; }",
        "static type changed: G.X")]
    [InlineData(
        "public struct G { public static int X = 1; }",
        "public struct G { public static int X = 2; }",
        "static initializer changed: G.X")]
    [InlineData(
        "public struct G { public static int X = 1; }",
        "public struct G { public static float X = 1; }",
        "static type changed: G.X")]
    [InlineData(
        "public class A { } public class B { }",
        "public class A { }",
        "type removed: B")]
    [InlineData(
        "public class A { } public class B { }",
        "public class A { } public struct B { public int V; }",
        "type kind changed: B")]
    [InlineData(
        "public enum E { One, Two }",
        "public enum E { Two, One }",
        "enum member removed or renumbered: E.One")]
    [InlineData(
        "public enum E { One, Two }",
        "public enum E { One }",
        "enum member removed or renumbered: E.Two")]
    public void RestartEdit_ReportsReason(string v1, string v2, string reason)
    {
        Assert.Contains(reason, RestartReasons(v1, v2));
    }

    [Theory]
    [InlineData( // static の値は保持 (initializer は同じ)
        "public class G { public static int X = 1; public int F() { return X; } }",
        "public class G { public static int X = 1; public int F() { return X + 1; } }")]
    [InlineData( // static の追加・削除
        "public class G { public static int X = 1; public static int Y = 2; }",
        "public class G { public static int X = 1; public static int Z = 4; }")]
    [InlineData( // instance field の追加・削除 (生きている instance を移す)
        "public class G { public int A = 1; }",
        "public class G { public int B = 2; }")]
    [InlineData( // 型と enum member の追加
        "public enum E { One }",
        "public enum E { One, Two } public class N { }")]
    public void LiveEdit_HasNoReason(string v1, string v2)
    {
        Assert.Empty(RestartReasons(v1, v2));
    }

    // method の改名 (呼び出し側も同じ build で作り直す) は live。名前は C# の識別子のまま
    [Fact]
    public void MethodRename_WithCallersRebuilt_IsLive()
    {
        var edited = Game.Replace("Use(", "Fetch(");
        Assert.NotEqual(Game, edited);

        Assert.Empty(RestartReasons(Game, edited));
    }

    // static method に overload を足すと既存の名前に引数の型が付く (`Make` → `Make__int`)。呼び出し側も作り直すので live
    [Fact]
    public void AddingStaticOverload_IsLive()
    {
        var edited = Edit("public static int Make(int v) { return v; }",
            "public static int Make(int v) { return v; }\n" +
            "    public static int Make(int v, int w) { return v + w; }");

        Assert.Empty(RestartReasons(Game, edited));
    }

    [Fact]
    public void AddingInstanceOverload_IsLive()
    {
        var edited = Edit("public int Use(int v) { return v; }",
            "public int Use(int v) { return v; }\n" +
            "    public int Use(int v, int w) { return v + w; }");

        Assert.Empty(RestartReasons(Game, edited));
    }

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
    // const は使う側に埋め込まれ、static の値は戻らない (実行中に変えた値が残る)
    [InlineData("public const int K = 1;", "public static int K = 1;",
        "static initializer changed: G.K")]
    [InlineData("public static int X = 1;", "public const int X = 1;",
        "static initializer changed: G.X")]
    public void RestartEdit_InGame_ReportsReason(string from, string to, string reason)
    {
        Assert.Contains(reason, RestartReasons(Game, Edit(from, to)));
    }

    // initializer は空白・comment・定数の書き方では変わらない
    [Theory]
    [InlineData("1 + 2;\n    public static int Z", "1+2;\n    public static int Z")]
    [InlineData("1 + 2;\n    public static int Z", "3;\n    public static int Z")]
    [InlineData("G.Make( 1 )", "G.Make(1 /* seed */)")]
    [InlineData("public int A = 1 + 2;", "public int A = 1+2;")]
    [InlineData("enum E { One, Two }", "enum E { One, Two, Three }")]
    [InlineData("F = 1f / 3f;", "F = 1f/3f;")]
    public void LiveEdit_InitializerSpellingIsIgnored(string from, string to)
    {
        Assert.Empty(RestartReasons(Game, Edit(from, to)));
    }

    // instance の形の変更と、副作用のある initializer の static の追加も live (reload が instance を移し、initializer を評価する)
    [Theory]
    [InlineData("public int A = 1 + 2;", "public int A = 1 + 2;\n    public int B = 4;")]
    [InlineData("public int A = 1 + 2;", "public float A = 1 + 2;")]
    [InlineData("public string S;", "public int[] S;")]
    [InlineData("record R(int X)", "record R(float X)")]
    [InlineData("public static int X = 1;",
        "public static int X = 1;\n    public static int W = G.Make(2);")]
    public void InstanceShapeAndNewStatic_AreLive(string from, string to)
    {
        Assert.Empty(RestartReasons(Game, Edit(from, to)));
    }

    // instance の field の初期化子の変更は live で、host に知らせる
    [Fact]
    public void InstanceFieldInitializerChange_IsANote()
    {
        var old = IlExport.Export([Game]);
        var next = IlExport.Export([Edit("public int A = 1 + 2;", "public int A = 4;")]);
        Assert.Equal(["instance field initializer changed: G.A"],
            TcsSelf.ReloadRules.Notes(TcsSelf.ReloadRules.TypesOf(old), TcsSelf.ReloadRules.TypesOf(next)));
    }
}
