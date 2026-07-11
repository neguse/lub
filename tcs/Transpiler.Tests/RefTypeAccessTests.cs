namespace TinyCs.Tests;

// --ref 型を通した host table へのアクセス規約を固定する。
// - instance method は colon call (`obj:method(...)`) で emit される
//   (lub の userdata method は self を第1引数に取るのでこの規約と一致する)
// - field 読みは `obj.field` の透過アクセス (onEvent の event table など)
public class RefTypeAccessTests
{
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
            Gfx = { readback = function() return rb end }
            {{result.Lua}}
            print(Game.Run())
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
            print(Game.Describe({ type = "key_down", keycode = 32 }))
            """;
        var output = TestHelper.RunLua(script).Trim();

        Assert.Equal("key_down:32", output);
    }
}
