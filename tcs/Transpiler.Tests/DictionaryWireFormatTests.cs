namespace TinyCs.Tests;

// T168: Dictionary<string, object> の wire format 契約を固定する。
// lub 等の host は Dictionary を --ref 関数へ渡された素の Lua table
// (文字列キー→値のみ、metatable / bookkeeping フィールドなし) として読む。
// Count は保存メタデータではなく pairs 走査で都度計算される。
public class DictionaryWireFormatTests
{
    private const string HostRefSource = """
        public class Handle
        {
            public int id;
        }

        public static class Host
        {
            public static Handle? create() => null;
            public static string inspect(
                System.Collections.Generic.Dictionary<string, object> opts) => "";
        }
        """;

    [Fact]
    public void HeterogeneousDictionary_PassedToRefFunction_IsPlainLuaTable()
    {
        var source = """
            using System.Collections.Generic;

            public static class Game
            {
                public static string Run()
                {
                    var handle = Host.create();
                    var opts = new Dictionary<string, object>
                    {
                        { "width", 640.0f },
                        { "title", "demo" },
                        { "vsync", true },
                        { "handle", handle! },
                        { "nested", new Dictionary<string, object> { { "x", 1.5f } } },
                        { "list", new List<float> { 1.0f, 2.0f } },
                    };
                    return Host.inspect(opts);
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [HostRefSource], checkNaming: false);
        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            local handle = { id = 7 }
            Host = {
              create = function() return handle end,
              inspect = function(t)
                if getmetatable(t) ~= nil then return "metatable" end
                local n = 0
                for k in pairs(t) do
                  if type(k) ~= "string" then return "nonstring-key:" .. tostring(k) end
                  n = n + 1
                end
                if n ~= 6 then return "count:" .. n end
                if t.width ~= 640.0 then return "width" end
                if t.title ~= "demo" then return "title" end
                if t.vsync ~= true then return "vsync" end
                if not rawequal(t.handle, handle) then return "handle" end
                if type(t.nested) ~= "table" or getmetatable(t.nested) ~= nil
                    or t.nested.x ~= 1.5 then return "nested" end
                if type(t.list) ~= "table" or t.list[1] ~= 1.0
                    or t.list[2] ~= 2.0 then return "list" end
                return "ok"
              end,
            }
            {{result.Lua}}
            print(Game.Run())
            """;

        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void DictionaryBuiltByAddAndIndexer_StaysPlainForRefFunction()
    {
        var source = """
            using System.Collections.Generic;

            public static class Game
            {
                public static string Run()
                {
                    var opts = new Dictionary<string, object>();
                    opts.Add("num", 1.5f);
                    opts["str"] = "s";
                    return Host.inspect(opts);
                }
            }
            """;

        var result = Transpiler.TranspileWithDiagnostics([source], null,
            [HostRefSource], checkNaming: false);
        Assert.True(result.Success, string.Join("\n", result.Errors));

        var script = $$"""
            Host = {
              create = function() return nil end,
              inspect = function(t)
                if getmetatable(t) ~= nil then return "metatable" end
                local n = 0
                for k in pairs(t) do
                  if type(k) ~= "string" then return "nonstring-key" end
                  n = n + 1
                end
                if n ~= 2 then return "count:" .. n end
                if t.num ~= 1.5 or t.str ~= "s" then return "values" end
                return "ok"
              end,
            }
            {{result.Lua}}
            print(Game.Run())
            """;

        Assert.Equal("ok", TestHelper.RunLua(script).Trim());
    }

    [Fact]
    public void HeterogeneousDictionary_CountIsComputedNotStored()
    {
        // Count が table 内のメタデータではなく pairs 走査で数えられること
        // (bookkeeping があると --ref 側の素 table 契約が壊れる)
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;

            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<string, object>();
                    d.Add("num", 1.5f);
                    d["str"] = "s";
                    d.Add("flag", true);
                    var before = d.Count;
                    d.Remove("flag");
                    return $"{before}:{d.Count}:{d.ContainsKey("str")}:{d.ContainsKey("flag")}";
                }
            }
            """, "T.Test()");
        Assert.Equal("3:2:true:false", result);
    }

    [Fact]
    public void HeterogeneousDictionary_ForeachSeesOnlyUserEntries()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;

            public class T
            {
                public static int Test()
                {
                    var d = new Dictionary<string, object>
                    {
                        { "a", 1 },
                        { "b", "x" },
                        { "c", true },
                    };
                    var n = 0;
                    foreach (var entry in d)
                    {
                        n++;
                    }
                    return n;
                }
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }
}
