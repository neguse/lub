namespace TinyCs.Tests;

// --ref 型を通した host table へのアクセス規約を固定する。
// - instance method は colon call (`obj:method(...)`) で emit される
//   (lub の userdata method は self を第1引数に取るのでこの規約と一致する)
// - field 読みは `obj.field` の透過アクセス (onEvent の event table など)
public class RefTypeAccessTests
{
    // namespace の中の参照専用型も、namespace を含めた小文字パスで引く
    // (Lub.Gfx → lub.gfx)。namespace 直下の enum は namespace の下に平らに
    // 置き (Lub.EventKind.Quit → lub.QUIT)、入れ子 enum は親の型の下に置く
    [Fact]
    public void NamespacedRefTypes_UseNamespaceInLuaPath()
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

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [refSource], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        var script = $$"""
            quitted = 0
            lub = {
              QUIT = 1000,
              gfx = { RGBA8 = 100, size = function() return 10 end },
              app = { quit = function() quitted = 1 end },
            }
            {{result.Lua}}
            print(Game.run() + quitted)
            """;
        Assert.Equal("1111", TestHelper.RunLua(script).Trim());
    }

    // namespace を写したパスの区切りが Lua の予約語になるときは、member と同じ
    // 規則で `_` を後置する (End → end_)
    [Fact]
    public void RefPathSegmentThatIsLuaKeyword_GetsUnderscoreSuffix()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using End;
            public static class Game { public static int Run() { return Api.Get(); } }
            """], null,
            ["namespace End { public static class Api { public static int Get() { return 0; } } }"],
            checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        var script = $$"""
            end_ = { api = { get = function() return 5 end } }
            {{result.Lua}}
            print(Game.run())
            """;
        Assert.Equal("5", TestHelper.RunLua(script).Trim());
    }

    // --ref パスの先頭 (namespace 由来の hostapi など) と同名のローカルは、
    // global の host table を隠さないよう改名する
    [Fact]
    public void LocalNamedLikeRefPathRoot_DoesNotShadowHostTable()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using HostApi;
            public static class Game
            {
                public static int Run() { var hostapi = 1; return hostapi + Screen.Get(); }
            }
            """], null,
            ["namespace HostApi { public static class Screen { public static int Get() { return 0; } } }"],
            checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));
        var script = $$"""
            hostapi = { screen = { get = function() return 10 end } }
            {{result.Lua}}
            print(Game.run())
            """;
        Assert.Equal("11", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void RefTypeInstanceMethod_EmitsColonCall()
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

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [refSource], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            local rb = {}
            function rb:status() return self == rb and "self-ok" or "self-ng" end
            gfx = { readback = function() return rb end }
            {{result.Lua}}
            print(Game.run())
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("self-ok", output);
    }

    [Fact]
    public void RefTypeField_ReadsHostTableFieldTransparently()
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

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [refSource], checkNaming: false);

        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            {{result.Lua}}
            print(Game.describe({ type = "key_down", keycode = 32 }))
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("key_down:32", output);
    }
}
