namespace TinyCs.Tests;

// --ref 型 (host) へのアクセスの IL の形を固定する (C と VM の host 関数の名前の元)。
// - static の呼び出しは namespace と入れ子の型を含めた小文字パスの foreign 関数
// - instance method は receiver の型を持つ foreign 関数
// - field 読みは host の object の field の読み
public class RefTypeAccessTests
{
    // namespace の中の参照専用型も、namespace を含めた小文字パスで引く (Lub.Gfx → lub.gfx)
    [Fact]
    public void NamespacedRefTypes_UseNamespaceInForeignNames()
    {
        var refSource = """
            namespace Lub
            {
                public enum EventKind { Quit = 1 }
                public static class Gfx
                {
                    public enum PixelFormat { Rgba8 = 1 }
                    public static int Size() { return 0; }
                }
                public static class App { public static void Quit() { } }
            }
            """;
        var source = """
            using Lub;
            public static class Game
            {
                public static int Run()
                {
                    App.Quit();
                    return Gfx.Size() + (int)Gfx.PixelFormat.Rgba8 + (int)EventKind.Quit;
                }
            }
            """;

        var result = IlExport.Export([source], referenceSources: [refSource]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal(["lub.app.quit", "lub.gfx.size"],
            result.ForeignMethods.Select(m => m.Name).OrderBy(n => n, StringComparer.Ordinal));
    }

    // namespace を写したパスの区切りが Lua の予約語になるときは、member と同じ
    // 規則で `_` を後置する (End → end_)
    [Fact]
    public void RefPathSegmentThatIsLuaKeyword_GetsUnderscoreSuffix()
    {
        var result = IlExport.Export(["""
            using End;
            public static class Game { public static int Run() { return Api.Get(); } }
            """], referenceSources:
            ["namespace End { public static class Api { public static int Get() { return 0; } } }"]);

        Assert.Empty(result.Diagnostics);
        Assert.Equal("end_.api.get", Assert.Single(result.ForeignMethods).Name);
    }

    // --ref パスの先頭 (namespace 由来の hostapi など) と同名のローカルも書ける
    [Fact]
    public void LocalNamedLikeRefPathRoot_IsAccepted()
    {
        var result = Checker.Check(["""
            using HostApi;
            public static class Game
            {
                public static int Run() { var hostapi = 1; return hostapi + Screen.Get(); }
            }
            """], null,
            ["namespace HostApi { public static class Screen { public static int Get() { return 0; } } }"]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void RefTypeInstanceMethod_HasReceiver()
    {
        var refSource = """
            public class Readback
            {
                public string status()
                {
                    return "";
                }
            }

            public static class Gfx
            {
                public static Readback? readback()
                {
                    return null;
                }
            }
            """;
        var source = """
            public static class Game
            {
                public static string Run()
                {
                    var rb = Gfx.readback();
                    return rb!.status();
                }
            }
            """;

        var result = IlExport.Export([source], referenceSources: [refSource]);

        Assert.Empty(result.Diagnostics);
        var status = result.ForeignMethods.Single(m => m.Receiver == "Readback");
        Assert.EndsWith(".status", status.Name);
        Assert.Equal("string", status.ReturnType);
        Assert.Null(result.ForeignMethods.Single(m => m.Name == "gfx.readback").Receiver);
    }

    [Fact]
    public void RefTypeField_ReadIsAccepted()
    {
        var refSource = """
            public class EventData
            {
                public string? type;
                public int keycode;
            }
            """;
        var source = """
            public static class Game
            {
                public static string Describe(EventData e)
                {
                    return e.type + ":" + e.keycode;
                }
            }
            """;

        var result = Checker.Check([source], null, [refSource]);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
        var export = IlExport.Export([source], referenceSources: [refSource]);
        Assert.Empty(export.Diagnostics);
        Assert.True(export.Classes.Single(c => c.Name == "EventData").IsExternal);
    }
}
