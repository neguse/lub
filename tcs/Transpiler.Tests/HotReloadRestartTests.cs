namespace TinyCs.Tests;

// 移行できない編集 (hot-reload-design §5.2 の restart) は、session の Update が
// 理由付きの restart にする
public class HotReloadRestartTests
{
    private static SessionUpdateResult Update(string v1, string v2)
    {
        var session = new IncrementalCompilationSession(instanceRegistry: true);
        session.OpenProject([("a.cs", v1)]);
        var result = session.Update("a.cs", v2);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        return result;
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
    public void RestartClassEdit_RequiresRestartWithReason(
        string v1, string v2, string reason)
    {
        var result = Update(v1, v2);

        Assert.True(result.RequiresRestart);
        Assert.Contains("a.cs: " + reason, result.RestartReasons);
    }

    // §5.2 で live の編集は restart にしない
    [Theory]
    [InlineData( // static の値は保持 (initializer は同じ)
        "public class G { public static int X = 1; public int F() { return X; } }",
        "public class G { public static int X = 1; public int F() { return X + 1; } }")]
    [InlineData( // static の追加・削除
        "public class G { public static int X = 1; public static int Y = 2; }",
        "public class G { public static int X = 1; public static int Z = 4; }")]
    [InlineData( // instance field の追加・削除 (registry で移行する)
        "public class G { public int A = 1; }",
        "public class G { public int B = 2; }")]
    [InlineData( // 型と enum member の追加
        "public enum E { One }",
        "public enum E { One, Two } public class N { }")]
    public void LiveEdit_DoesNotRequireRestart(string v1, string v2)
    {
        var result = Update(v1, v2);

        Assert.False(result.RequiresRestart, string.Join("\n", result.RestartReasons));
        Assert.Empty(result.RestartReasons);
    }
}
