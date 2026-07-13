using TinyCs;

namespace TinyCs.Tests;

// doc/incremental-module-compilation-design.md §11.3-§11.4, §13.1 (M3) の
// 受入テスト。atomic apply / rollback (presence sentinel)、commit ACK、
// restart classification、lume.hotswap 経由の end-to-end を検証する。
public class ModuleTransactionTests
{
    private static string TinySystemLua => LuaRuntime.LoadTinySystemSource();

    private static string RegistryLua =>
        LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath);

    private static IncrementalCompilationSession Open(
        params (string Path, string Text)[] files)
    {
        var session = new IncrementalCompilationSession();
        session.OpenProject(files);
        return session;
    }

    private static string Snapshot(IncrementalCompilationSession session,
        string? entry = null, bool ack = false) =>
        ModuleLinker.LinkSnapshot(session.Artifacts, session.Revision, entry,
            TinySystemLua, RegistryLua, ack);

    // ------------------------------------------------------------------
    // restart classification (§11.3)

    private const string VecCs = """
        public class Vec
        {
            public int x = 1;
            public static int Count = 0;
            public int Get() { return x; }
        }
        """;

    [Fact]
    public void BodyEditAndMemberAddAreLiveSafe()
    {
        var session = Open(("vec.cs", VecCs));
        var r1 = session.Update("vec.cs", VecCs.Replace("return x;", "return x + 1;"));
        Assert.True(r1.FastPath);
        Assert.False(r1.RequiresRestart);

        var r2 = session.Update("vec.cs", VecCs.Replace(
            "public int Get() { return x; }",
            "public int Get() { return x; }\n    public int More() { return 2; }"));
        Assert.False(r2.FastPath);
        Assert.True(r2.Success);
        Assert.False(r2.RequiresRestart);
    }

    [Theory]
    [InlineData("public static int Count = 0;", "public static int Count = 5;",
        "static initializer changed")]
    [InlineData("public int x = 1;", "public int x = 1;\n    public int y = 2;",
        "instance shape changed")]
    [InlineData("public class Vec", "public class Vec : Base",
        "base changed")]
    public void StateShapeChangesRequireRestart(string from, string to,
        string reason)
    {
        var baseCs = "public class Base { }\n" + VecCs;
        var session = Open(("vec.cs", baseCs));
        var r = session.Update("vec.cs", baseCs.Replace(from, to));
        Assert.True(r.Success);
        Assert.True(r.RequiresRestart);
        Assert.Contains(r.RestartReasons, m => m.Contains(reason));
    }

    [Fact]
    public void TypeRemovalAndImpureNewStaticRequireRestart()
    {
        var two = VecCs + "\npublic class Extra { public static int E() { return 1; } }";
        var session = Open(("vec.cs", two));
        var r = session.Update("vec.cs", VecCs);
        Assert.True(r.RequiresRestart);
        Assert.Contains(r.RestartReasons, m => m.Contains("type removed: Extra"));

        var session2 = Open(("vec.cs", VecCs));
        var r2 = session2.Update("vec.cs", VecCs.Replace(
            "public static int Count = 0;",
            "public static int Count = 0;\n    public static int Seed = Compute();\n    public static int Compute() { return 3; }"));
        Assert.True(r2.RequiresRestart);
        Assert.Contains(r2.RestartReasons, m => m.Contains("impure new static: Vec.Seed"));

        // 純粋 (定数) の新規 static は live-safe
        var session3 = Open(("vec.cs", VecCs));
        var r3 = session3.Update("vec.cs", VecCs.Replace(
            "public static int Count = 0;",
            "public static int Count = 0;\n    public static int Pureness = 9;"));
        Assert.True(r3.Success);
        Assert.False(r3.RequiresRestart);
    }

    // ------------------------------------------------------------------
    // transaction rollback (§11.4, §18.2 failed override-add)

    private static string RegistryPath =>
        TestHelper.FindProjectFile(LuaRuntime.RegistryRelativePath);

    [Fact]
    public void FailedBatchRollsBackAndBasePatchStillReachesDerived()
    {
        // rev1: B.M / D (override なし)。rev2: D.M 追加 + 別 module が define で
        // 失敗 → 全 rollback。D.M が own key として残らないこと (presence
        // sentinel)。rev3: B.M だけ patch → D:M() が新実装を見ること。
        var output = TestHelper.RunLua($$"""
            local Reg = dofile("{{RegistryPath}}")
            local reg = Reg.new(_G)
            local function mod_b(hash, body)
              return { id = "b", hash = hash,
                types = { { id = "b#B", name = "B", kind = "class",
                  statics = {}, keys = { "__index", "M" } } },
                define = function(_ENV)
                  B.__index = B
                  function B:M() return body end
                end,
                inits = {}, initfns = {} }
            end
            local function mod_d(hash, with_override)
              local keys = { "__index" }
              if with_override then keys[#keys + 1] = "M" end
              return { id = "d", hash = hash,
                types = { { id = "d#D", name = "D", kind = "class", base = "B",
                  statics = {}, keys = keys } },
                define = function(_ENV)
                  D.__index = D
                  setmetatable(D, { __index = B })
                  if with_override then
                    function D:M() return "override" end
                  end
                end,
                inits = {}, initfns = {} }
            end
            local bomb = { id = "x", hash = "x1",
              types = { { id = "x#X", name = "X", kind = "class",
                statics = {}, keys = {} } },
              define = function(_ENV) error("boom") end,
              inits = {}, initfns = {} }

            reg:applyBatch({ revision = 1, modules = { mod_b("b1", "v1"), mod_d("d1", false) } })
            local D = reg.types["d#D"]
            local B = reg.types["b#B"]
            local ok, err = pcall(function()
              reg:applyBatch({ revision = 2,
                modules = { mod_b("b1", "v1"), mod_d("d2", true), bomb } })
            end)
            print(ok, err)
            print(rawget(D, "M") == nil)          -- override は rollback で消える
            print(reg.types["x#X"] == nil, reg.alias["X"] == nil)
            print(reg.revision)
            -- rev3: B.M だけ patch。D は inherited lookup で新実装を見る
            reg:applyBatch({ revision = 3, modules = { mod_b("b2", "v3"), mod_d("d1", false) } })
            print(D:M())
            print(B == reg.types["b#B"], D == reg.types["d#D"])
            """).Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.StartsWith("false", output[0]);
        Assert.Contains("boom", output[0]);
        Assert.Equal("true", output[1]);
        Assert.Equal("true\ttrue", output[2]);
        Assert.Equal("1", output[3]);
        Assert.Equal("v3", output[4]);
        Assert.Equal("true\ttrue", output[5]);
    }

    [Fact]
    public void FailedBatchRestoresStaticsMethodsAndMetatable()
    {
        var output = TestHelper.RunLua($$"""
            local Reg = dofile("{{RegistryPath}}")
            local reg = Reg.new(_G)
            local function mod(hash, val, extra)
              local keys = { "__index", "Get" }
              if extra then keys[#keys + 1] = "Extra" end
              return { id = "m", hash = hash,
                types = { { id = "m#T", name = "T", kind = "class",
                  statics = { { key = "S", default = 0, pure = true } },
                  keys = keys } },
                define = function(_ENV)
                  T.__index = T
                  function T:Get() return val end
                  if extra then function T:Extra() return 1 end end
                end,
                inits = { ["m#T"] = function(_ENV) T.S = 10 end },
                initfns = { ["m#T"] = { S = function(_ENV) T.S = 10 end } } }
            end
            local bomb = { id = "x", hash = "x1",
              types = { { id = "x#X", name = "X", kind = "class",
                statics = {}, keys = {} } },
              define = function(_ENV) error("late boom") end,
              inits = {}, initfns = {} }

            reg:applyBatch({ revision = 1, modules = { mod("h1", "old", false) } })
            local T = reg.types["m#T"]
            T.S = 77 -- 実行中に進んだ static
            local get1 = T.Get
            local mt1 = getmetatable(T)
            pcall(function()
              reg:applyBatch({ revision = 2, modules = { mod("h2", "new", true), bomb } })
            end)
            print(T.S)                      -- static は rollback で 77 のまま
            print(T.Get == get1)            -- method も旧実装に戻る
            print(rawget(T, "Extra") == nil)
            print(getmetatable(T) == mt1)
            local inst = setmetatable({}, T)
            print(inst:Get())
            """).Trim().Split('\n').Select(l => l.Trim()).ToArray();
        Assert.Equal(["77", "true", "true", "true", "old"], output);
    }

    // ------------------------------------------------------------------
    // commit ACK (§13.1)

    [Fact]
    public void AckLineIsEmittedOnCommitAndOnFailure()
    {
        var session = Open(("vec.cs", VecCs));
        var snap = Snapshot(session, "Vec", ack: true);
        var f = Path.GetTempFileName();
        File.WriteAllText(f, snap);
        try
        {
            var output = TestHelper.RunLua($$"""
                dofile("{{f.Replace("\\", "/")}}")
                local reg = _G.__tcs_module_runtime.registry
                pcall(function()
                  reg:applyBatch({ revision = 9, ack = true, modules = { {
                    id = "vec.cs", hash = "zzz",
                    types = { { id = "vec.cs#Vec", name = "Vec", kind = "class",
                      statics = {}, keys = {} } },
                    define = function(_ENV) error("bad") end,
                    inits = {}, initfns = {} } } })
                end)
                """).Trim();
            var lines = output.Split('\n').Select(l => l.Trim()).ToArray();
            Assert.Contains(lines, l =>
                l.StartsWith("@@tcs_commit") && l.Contains("\"revision\":1")
                && l.Contains("\"ok\":true"));
            Assert.Contains(lines, l =>
                l.StartsWith("@@tcs_commit") && l.Contains("\"revision\":9")
                && l.Contains("\"ok\":false") && l.Contains("bad"));
        }
        finally
        {
            File.Delete(f);
        }
    }

    // ------------------------------------------------------------------
    // lume.hotswap end-to-end (§18.2)。lume 2.3.0 の hotswap 実装を忠実に
    // 再現した harness で、bridge snapshot の reload 契約を検証する。

    private static string LumeHotswapHarness => """
        local function clone(t)
          local rtn = {}
          for k, v in pairs(t) do rtn[k] = v end
          return rtn
        end
        local function hotswap(modname)
          local oldglobal = clone(_G)
          local updated = {}
          local function update(old, new)
            if updated[old] then return end
            updated[old] = true
            local oldmt, newmt = getmetatable(old), getmetatable(new)
            if oldmt and newmt then update(oldmt, newmt) end
            for k, v in pairs(new) do
              if type(v) == "table" then update(old[k], v) else old[k] = v end
            end
          end
          local err = nil
          local function onerror(e)
            for k in pairs(_G) do _G[k] = oldglobal[k] end
            err = e
          end
          local ok, oldmod = pcall(require, modname)
          oldmod = ok and oldmod or nil
          xpcall(function()
            package.loaded[modname] = nil
            local newmod = require(modname)
            if type(oldmod) == "table" then update(oldmod, newmod) end
            for k, v in pairs(oldglobal) do
              if v ~= _G[k] and type(v) == "table" then
                update(v, _G[k])
                _G[k] = v
              end
            end
          end, onerror)
          package.loaded[modname] = oldmod
          if err then return nil, err end
          return oldmod
        end
        """;

    [Fact]
    public void LumeHotswapReloadKeepsIdentityAndAppliesBodyEdit()
    {
        var session = Open(("vec.cs", VecCs));
        var snap1 = Snapshot(session, "Vec");
        var r = session.Update("vec.cs", VecCs.Replace("return x;", "return x * 100;"));
        Assert.True(r.Success && r.FastPath);
        var snap2 = Snapshot(session, "Vec");
        // 失敗 batch: revision を進め、module hash を変え、define を爆破する
        var snap3 = snap2
            .Replace("revision = 2,", "revision = 3,")
            .Replace("hash = \"", "hash = \"X")
            .Replace("define = function(_ENV)", "define = function(_ENV) error('boom')");

        var dir = Directory.CreateTempSubdirectory("tcs-hotswap");
        try
        {
            var modPath = Path.Combine(dir.FullName, "entrymod.lua");
            File.WriteAllText(modPath, snap1);
            var script = $$"""
                package.path = "{{dir.FullName.Replace("\\", "/")}}/?.lua;" .. package.path
                {{LumeHotswapHarness}}
                local host = io.open("{{modPath.Replace("\\", "/")}}", "r")
                host:close()
                local wrapper = require("entrymod")
                local reg = _G.__tcs_module_runtime.registry
                local Vec = reg.types["vec.cs#Vec"]
                -- 実行中に増えた live state (runtime-added key は走査/削除されない)
                Vec.BigState = {}
                for i = 1, 200000 do Vec.BigState[i] = i end
                local inst = Vec.new()
                print(inst:Get())
                local write = io.open("{{modPath.Replace("\\", "/")}}", "w")
                write:write({{LuaLongString("SNAP2")}})
                write:close()
                local t0 = os.clock()
                local old, err = hotswap("entrymod")
                local elapsed = os.clock() - t0
                print(old == wrapper, err)
                print(inst:Get())
                print(Vec == reg.types["vec.cs#Vec"])
                print(#Vec.BigState)
                print(elapsed < 0.5)
                -- 失敗する reload: old module / registry 状態が残ること
                write = io.open("{{modPath.Replace("\\", "/")}}", "w")
                write:write({{LuaLongString("SNAP3")}})
                write:close()
                local old2, err2 = hotswap("entrymod")
                print(old2 == nil, err2 ~= nil and string.find(err2, "boom") ~= nil)
                print(inst:Get())
                print(reg.revision)
                """
                .Replace(LuaLongString("SNAP2"), LuaLongString(snap2))
                .Replace(LuaLongString("SNAP3"), LuaLongString(snap3));
            var output = TestHelper.RunLua(script, TimeSpan.FromSeconds(30))
                .Trim().Split('\n').Select(l => l.Trim()).ToArray();
            Assert.Equal("1", output[0]);
            Assert.Equal("true\tnil", output[1]);  // wrapper identity + 成功
            Assert.Equal("100", output[2]);        // 既存 instance に新 body
            Assert.Equal("true", output[3]);       // type table identity
            Assert.Equal("200000", output[4]);     // live state 保持
            Assert.Equal("true", output[5]);       // 走査が live state に比例しない
            Assert.Equal("true\ttrue", output[6]); // 失敗 reload は nil + boom
            Assert.Equal("100", output[7]);        // last-good の挙動を維持
            Assert.Equal("2", output[8]);          // revision も last-good のまま
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // Lua long string literal ([==[ ... ]==])。snapshot 本文を script へ
    // 埋め込むためレベルは本文と衝突しない値を選ぶ。
    private static string LuaLongString(string s)
    {
        var level = "=====";
        return $"[{level}[\n{s}]{level}]";
    }
}
