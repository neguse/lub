namespace TinyCs.Tests;

// reload chunk で移行できない編集 (hot-reload-design §5.2 の restart) は
// chunk を作らず、理由付きの restart にする
public class HotReloadRestartTests
{
    [Theory]
    [InlineData(
        "public class G { public static int X = 1; }",
        "public class G { public static int X = 2; }",
        "static initializer changed: G.x")]
    [InlineData(
        "public class G { public static int X; }",
        "public class G { public static int X = 2; }",
        "static initializer changed: G.x")]
    [InlineData(
        "public class G { public static int P { get; set; } = 1; }",
        "public class G { public static int P { get; set; } = 2; }",
        "static initializer changed: G.p")]
    [InlineData( // const は使う側に埋め込まれるが、static の initializer は再評価されない
        "public class G { public const int K = 1; public static int X = K * 2; }",
        "public class G { public const int K = 2; public static int X = K * 2; }",
        "static initializer changed: G.k")]
    [InlineData(
        "public class A { } public class B { } public class C : A { }",
        "public class A { } public class B { } public class C : B { }",
        "base changed: C")]
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
        "enum member removed or renumbered: E.ONE")]
    [InlineData(
        "public enum E { One, Two }",
        "public enum E { One }",
        "enum member removed or renumbered: E.TWO")]
    public void Plan_RestartClassEdit_ReportsReasonWithoutChunk(
        string v1, string v2, string reason)
    {
        var plan = HotReload.PlanReload([v1], [v2]);

        Assert.Null(plan.Chunk);
        Assert.Contains(reason, plan.RestartReasons);
        var ex = Assert.Throws<InvalidOperationException>(
            () => HotReload.EmitReloadChunk([v1], [v2]));
        Assert.Contains(reason, ex.Message);
    }

    // §5.2 で live の編集は restart にしない
    [Theory]
    [InlineData( // static の値は保持 (initializer は同じ)
        "public class G { public static int X = 1; public int F() { return X; } }",
        "public class G { public static int X = 1; public int F() { return X + 1; } }")]
    [InlineData( // static の追加・削除・型変更
        "public class G { public static int X = 1; public static int Y = 2; }",
        "public class G { public static float X = 3f; public static int Z = 4; }")]
    [InlineData( // instance field の追加・削除 (registry で移行する)
        "public class G { public int A = 1; }",
        "public class G { public int B = 2; }")]
    [InlineData( // 型と enum member の追加
        "public enum E { One }",
        "public enum E { One, Two } public class N { }")]
    public void Plan_LiveEdit_EmitsChunk(string v1, string v2)
    {
        var plan = HotReload.PlanReload([v1], [v2]);

        Assert.Empty(plan.RestartReasons);
        Assert.NotNull(plan.Chunk);
    }
}
