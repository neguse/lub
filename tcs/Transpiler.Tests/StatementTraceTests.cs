using System.Diagnostics;
using TinyCs;

namespace TinyCs.Tests;

// --trace: 実行した C# 行を global の __tcs_trace に記録し、関数ごとの local
// __tcs_at で現在行を持つ (host が debug.getlocal で C# の呼び出し元を組み立てる)
public class StatementTraceTests
{
    private const string GamePath = "samples/game/Game.cs";
    private static readonly string[] GamePrefix = ["samples/game/"];

    // __tcs_trace の key を並べて 1 行にする
    private const string PrintTraceKeys = """
        local keys = {}
        for k in pairs(__tcs_trace) do keys[#keys + 1] = k end
        table.sort(keys)
        print(table.concat(keys, ","))
        """;

    // debug.getlocal で各 frame の __tcs_at を集める host 関数 (呼び出し元が先)。
    // --ref の型は host の table で、Lua 名は host API の流儀 (host.stack)
    private const string HostStub = """
        host = {}
        function host.stack()
          local out = {}
          local level = 2
          while debug.getinfo(level, "f") do
            local i = 1
            while true do
              local name, value = debug.getlocal(level, i)
              if name == nil then break end
              if name == "__tcs_at" then out[#out + 1] = tostring(value) end
              i = i + 1
            end
            level = level + 1
          end
          return table.concat(out, " < ")
        end
        """;

    // __tcs_trace の key の行番号を数の順に 1 行にする
    private const string PrintTraceLines = """
        local lines = {}
        for k in pairs(__tcs_trace) do lines[#lines + 1] = tonumber(k:match(":(%d+)$")) end
        table.sort(lines)
        print(table.concat(lines, ","))
        """;

    // 呼び出し元 frame の __tcs_at を呼ぶたびに marks へ積む host 関数
    private const string MarkStub = """
        host = {}
        marks = {}
        function reset_marks() marks = {} end
        local function mark()
          local i = 1
          while true do
            local name, value = debug.getlocal(3, i)
            if name == nil then break end
            if name == "__tcs_at" then marks[#marks + 1] = tostring(value) end
            i = i + 1
          end
        end
        function host.mark(v) mark(); return v end
        function host.less(a, b) mark(); return a < b end
        """;

    private const string MarkHost = """
        public static class Host
        {
            public static int Mark(int v) { return v; }
            public static bool Less(int a, int b) { return a < b; }
        }
        """;

    // marks を 1 行にする (行番号だけ)
    private const string PrintMarks = """
        local lines = {}
        for i, m in ipairs(marks) do lines[i] = m:match(":(%d+)$") end
        print(table.concat(lines, ","))
        """;

    private static string Transpile(string[] sources, string[] paths,
        string[]? refs = null, bool instanceRegistry = false)
    {
        var result = Transpiler.TranspileWithDiagnostics(sources, paths,
            referenceSources: refs, instanceRegistry: instanceRegistry,
            tracePrefixes: GamePrefix);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        return result.Lua;
    }

    [Fact]
    public void Trace_RecordsExecutedLinesOfTracedFileOnly()
    {
        const string game = """
            public class Game
            {
                public static int Run(int v)
                {
                    int r = Lib.Twice(v);
                    if (v > 100)
                    {
                        r = r + 1;
                    }
                    else
                    {
                        r = r - 1;
                    }
                    return r;
                }
            }
            """;
        const string lib = """
            public class Lib
            {
                public static int Twice(int x)
                {
                    return x * 2;
                }
            }
            """;
        var lua = Transpile([game, lib], [GamePath, "cs-lib/lubx/Lib.cs"]);
        var output = TestHelper.RunLua($"""
            {lua}
            print(Game.Run(3))
            {PrintTraceKeys}
            print(_G.__tcs_at)
            """).Trim().Split('\n');
        Assert.Equal("5", output[0]);
        // 実行した行だけ。取らなかった if の枝 (8 行目) と Lib.cs は入らない
        Assert.Equal(
            $"{GamePath}:12,{GamePath}:14,{GamePath}:5,{GamePath}:6",
            output[1]);
        // __tcs_at は関数の local で、global には漏れない
        Assert.Equal("nil", output[2]);
    }

    // `return f(x)` は Lua の tail call で、その frame は stack に残らない。
    // 呼び出し元の行を残したい箇所では値を local に受けてから return する
    [Fact]
    public void Trace_CallerLinesAreReadableFromEachFrame()
    {
        const string game = """
            using System;
            public class Game
            {
                public static string Outer()
                {
                    string s = Inner(1);
                    return s;
                }
                public static string Inner(int v)
                {
                    string s = Host.Stack();
                    return s;
                }
                public static string ViaLambda()
                {
                    Func<int, string> f = x =>
                    {
                        string s = Host.Stack();
                        return s;
                    };
                    string r = f(1);
                    return r;
                }
                public static string TailReturn()
                {
                    return Inner(1);
                }
            }
            """;
        const string host = """
            public static class Host { public static string Stack() { return ""; } }
            """;
        var lua = Transpile([game], [GamePath], refs: [host]);
        var output = TestHelper.RunLua($"""
            {HostStub}
            {lua}
            print(Game.Outer())
            print(Game.ViaLambda())
            print(Game.TailReturn())
            """).Trim().Split('\n');
        Assert.Equal($"{GamePath}:11 < {GamePath}:6", output[0]);
        Assert.Equal($"{GamePath}:18 < {GamePath}:21", output[1]);
        // tail call した TailReturn の frame は無い
        Assert.Equal($"{GamePath}:11", output[2]);
    }

    // elseif の条件は前の arm が偽のときだけ評価されるので、その行の trace 点が要る
    [Fact]
    public void Trace_ElseIfConditionRunsAtItsOwnLine()
    {
        const string game = """
            public class Game
            {
                public static int Pick(int v)
                {
                    if (Host.Less(v, 0))
                    {
                        return 1;
                    }
                    else if (Host.Less(v, 5))
                    {
                        return 2;
                    }
                    return 3;
                }
                public static int PickLowered(int v)
                {
                    if (Host.Less(v, 0))
                    {
                        return 1;
                    }
                    else if (v > 0 ? Host.Less(v, 5) : false)
                    {
                        return 2;
                    }
                    return 3;
                }
            }
            """;
        var lua = Transpile([game], [GamePath], refs: [MarkHost]);
        var output = TestHelper.RunLua($"""
            {MarkStub}
            {lua}
            print(Game.Pick(3))
            {PrintMarks}
            {PrintTraceLines}
            reset_marks()
            for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
            print(Game.PickLowered(3))
            {PrintMarks}
            {PrintTraceLines}
            """).Trim().Split('\n');
        Assert.Equal(["2", "5,9", "5,9,11", "2", "17,21", "17,21,23"], output);
    }

    // while / do-while の条件は反復ごとに評価されるので、毎回その行に戻す
    [Fact]
    public void Trace_LoopConditionRunsAtItsOwnLineOnEveryIteration()
    {
        const string game = """
            public class Game
            {
                public static int Loops()
                {
                    int n = 0;
                    while (Host.Less(n, 2))
                    {
                        n = n + 1;
                    }
                    int m = 0;
                    do
                    {
                        m = m + 1;
                    } while (Host.Less(m, 2));
                    return n + m;
                }
                public static int LoopsLowered()
                {
                    int n = 0;
                    while (n >= 0 ? Host.Less(n, 2) : false)
                    {
                        n = n + 1;
                    }
                    int m = 0;
                    do
                    {
                        m = m + 1;
                    } while (m >= 0 ? Host.Less(m, 2) : false);
                    return n + m;
                }
            }
            """;
        var lua = Transpile([game], [GamePath], refs: [MarkHost]);
        var output = TestHelper.RunLua($"""
            {MarkStub}
            {lua}
            print(Game.Loops())
            {PrintMarks}
            reset_marks()
            print(Game.LoopsLowered())
            {PrintMarks}
            {PrintTraceLines}
            print(_G.__tcs_at)
            """).Trim().Split('\n');
        Assert.Equal([
            "4", "6,6,6,14,14",
            "4", "20,20,20,28,28",
            "5,6,8,10,11,13,14,15,19,20,22,24,25,27,28,29",
            "nil",
        ], output);
    }

    // 式本体の setter / ctor の式も文として trace する
    [Fact]
    public void Trace_ExpressionBodiedSetterAndCtorAreTraced()
    {
        const string game = """
            public class Game
            {
                private int _v;
                public Game(int v) => _v = Host.Mark(v);
                public int V
                {
                    get { return _v; }
                    set => _v = Host.Mark(value);
                }
            }
            """;
        var lua = Transpile([game], [GamePath], refs: [MarkHost]);
        var output = TestHelper.RunLua($"""
            {MarkStub}
            {lua}
            local g = Game.new(1)
            g:set_V(2)
            print(g:get_V())
            {PrintMarks}
            {PrintTraceLines}
            """).Trim().Split('\n');
        Assert.Equal(["2", "4,8", "4,7,8"], output);
    }

    // method 以外の関数 (ctor / accessor / 式本体の property / operator / struct ctor)
    // も本体の先頭で __tcs_at を local に宣言して trace する
    [Fact]
    public void Trace_NonMethodFunctionsAreTraced()
    {
        const string game = """
            public class Game
            {
                private int _v;
                public Game(int v)
                {
                    int t = v;
                    _v = t;
                }
                public int V
                {
                    get { return _v; }
                    set { _v = value; }
                }
                public int Twice => _v * 2;
                public static Game operator +(Game a, Game b)
                {
                    int s = a._v + b._v;
                    return new Game(s);
                }
            }
            public struct Vec
            {
                public int X;
                public Vec(int x)
                {
                    X = x;
                }
            }
            """;
        var lua = Transpile([game], [GamePath]);
        var output = TestHelper.RunLua($"""
            {lua}
            local g = Game.new(1)
            {PrintTraceLines}
            for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
            g:set_V(2)
            print(g:get_V())
            {PrintTraceLines}
            for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
            print(g:get_Twice())
            {PrintTraceLines}
            for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
            print((g + Game.new(3)):get_V())
            {PrintTraceLines}
            for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
            print(Vec.ctor(4).X)
            {PrintTraceLines}
            print(_G.__tcs_at)
            """).Trim().Split('\n');
        Assert.Equal([
            "6,7",
            "2", "11,12",
            "4", "14",
            "5", "6,7,11,17,18",
            "4", "26",
            "nil",
        ], output);
    }

    // 1 つの文の trace 行は 1 回: 前宣言 (is-pattern の designation) を持つ文 (elseif も) と、
    // do ... end に閉じる return (continue label が後に続く)。
    // __tcs_trace を値を持たない proxy にして、行ごとの書き込みを数える
    [Fact]
    public void Trace_EachStatementWritesTraceOnce()
    {
        const string game = """
            public class Derived { public int N = 4; }
            public class Game
            {
                public static int Run()
                {
                    object b = new Derived();
                    int r = 0;
                    if (b is Derived d)
                        r = d.N;
                    if (r < 0)
                        r = 0;
                    else if (b is Derived e)
                        r = r + e.N;
                    return r;
                }
                public static int Cont(int v)
                {
                    int i = 0;
                    while (i < 3)
                    {
                        i = i + 1;
                        if (i == v) continue;
                        return i;
                    }
                    return -1;
                }
            }
            """;
        var lua = Transpile([game], [GamePath]);
        var output = TestHelper.RunLua($$"""
            local writes = setmetatable({}, { __index = function() return 0 end })
            __tcs_trace = setmetatable({}, { __newindex = function(_, k) writes[k] = writes[k] + 1 end })
            {{lua}}
            print(Game.Run(), Game.Cont(1))
            print(writes["{{GamePath}}:8"], writes["{{GamePath}}:12"], writes["{{GamePath}}:23"])
            """).Trim().Split('\n');
        Assert.Equal(["8\t2", "1\t1\t1"], output);
    }

    // --trace 付きの session で v1 の snapshot と、v2 を当てる batch を作る
    private static (string Snapshot, string Batch) TracedReload(string v1, string v2)
    {
        var session = new IncrementalCompilationSession(instanceRegistry: true,
            tracePrefixes: GamePrefix);
        session.OpenProject([(GamePath, v1)]);
        var from = session.Revision;
        var snapshot = ModuleLinker.LinkSnapshot(session.Artifacts, from, null,
            LuaRuntime.LoadTinySystemSource(),
            LuaRuntime.LoadRuntimeFile(LuaRuntime.RegistryRelativePath));
        var r = session.Update(GamePath, v2);
        Assert.True(r.Success && !r.RequiresRestart, string.Join("\n", r.Errors.Concat(r.RestartReasons)));
        return (snapshot, ModuleLinker.LinkBatch(session.Artifacts, [GamePath], session.Revision,
            from, null, migration: session.MigrationFrom(from)));
    }

    [Fact]
    public void Trace_ReloadTracesChangedBodyWithRealPath()
    {
        const string v1 = """
            public class Game
            {
                public int Score()
                {
                    return 1;
                }
            }
            """;
        const string v2 = """
            public class Game
            {
                public int Score()
                {
                    int s = 2;
                    return s;
                }
            }
            """;
        var (snapshot, batch) = TracedReload(v1, v2);
        var output = TestHelper.RunLua($"""
            {TestHelper.LoadChunk(snapshot, "v1")}
            local Game = __tcs_module_runtime.registry.env.Game
            local g = Game.new()
            g:Score()
            local trace = __tcs_trace
            assert(trace["{GamePath}:5"], "v1 traced")
            for k in pairs(trace) do trace[k] = nil end
            {TestHelper.LoadChunk(batch, "reload")}
            assert(__tcs_trace == trace, "the batch keeps the table")
            print(g:Score())
            {PrintTraceKeys}
            """).Trim().Split('\n');
        Assert.Equal("2", output[0]);
        Assert.Equal($"{GamePath}:5,{GamePath}:6", output[1]);
    }

    // host が game の static 状態を見つけるための型 table の登録 (__tcs_trace_classes)
    [Fact]
    public void Trace_RegistersTypeTablesOfTracedFileOnly()
    {
        const string game = """
            public static class Config { public static int Level = 3; }
            public class Player { public int Hp = 1; }
            public struct Vec { public int X; }
            public enum Mode { A, B }
            """;
        const string lib = """
            public class Lib { public static int N = 1; }
            """;
        var lua = Transpile([game, lib], [GamePath, "cs-lib/lubx/Lib.cs"]);
        var output = TestHelper.RunLua($"""
            {lua}
            local c = __tcs_trace_classes
            assert(c.Config == Config and c.Config.Level == 3, "static class")
            assert(c.Player == Player and getmetatable(Player.new()) == c.Player, "class")
            assert(c.Vec == Vec and c.Vec.new().X == 0, "struct")
            assert(c.Lib == nil, "untraced file")
            local n = 0
            for _ in pairs(c) do n = n + 1 end
            print(n)
            """).Trim();
        // enum は状態を持たないので登録しない
        Assert.Equal("3", output);
    }

    [Fact]
    public void Trace_ReloadKeepsRegisteredIdentityAndRegistersNewClass()
    {
        const string v1 = """
            public class Game { public static int Score = 1; }
            """;
        const string v2 = """
            public class Game { public static int Score = 1; }
            public class Boss { public static int Hp = 9; }
            """;
        var (snapshot, batch) = TracedReload(v1, v2);
        var output = TestHelper.RunLua($"""
            {TestHelper.LoadChunk(snapshot, "v1")}
            local env = __tcs_module_runtime.registry.env
            local registry = __tcs_trace_classes
            local game = env.Game
            assert(registry.Game == game)
            {TestHelper.LoadChunk(batch, "reload")}
            assert(__tcs_trace_classes == registry, "registry table kept")
            assert(env.Game == game, "class identity kept")
            assert(registry.Game == game, "registered table follows the kept identity")
            assert(registry.Boss == env.Boss and env.Boss.Hp == 9, "new class registered")
            print("ok")
            """).Trim();
        Assert.Equal("ok", output);
    }

    [Fact]
    public void Trace_PrefixIsMatchedAgainstNormalizedPath()
    {
        const string game = """
            public class Game
            {
                public static int Run() { return 1; }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([game],
            [@"samples\game\Game.cs"], tracePrefixes: GamePrefix);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        var output = TestHelper.RunLua($"""
            {result.Lua}
            Game.Run()
            {PrintTraceKeys}
            """).Trim();
        Assert.Equal($"{GamePath}:3", output);
    }

    // prefix は path の segment 単位で比べる (ファイルそのものか、その directory)
    [Theory]
    [InlineData("samples/game", "samples/game/Game.cs:3")]
    [InlineData("samples/game/", "samples/game/Game.cs:3")]
    [InlineData("./samples/game/", "samples/game/Game.cs:3")]
    [InlineData("samples/game/Game.cs", "samples/game/Game.cs:3")]
    [InlineData("samples/game/Game", "")]
    [InlineData("samples/gameover", "samples/gameover/Over.cs:3")]
    [InlineData("./", "samples/game/Game.cs:3,samples/gameover/Over.cs:3")]
    public void Trace_PrefixMatchesWholePathSegments(string prefix, string expected)
    {
        const string game = """
            public class Game
            {
                public static int Run() { return 1; }
            }
            """;
        const string over = """
            public class Over
            {
                public static int Run() { return 2; }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([game, over],
            ["samples/game/Game.cs", "samples/gameover/Over.cs"], tracePrefixes: [prefix]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        var output = TestHelper.RunLua($"""
            {result.Lua}
            Game.Run()
            Over.Run()
            {PrintTraceKeys}
            """).Trim();
        Assert.Equal(expected, output);
    }

    [Fact]
    public void NoTrace_OutputHasNoTraceCode()
    {
        const string game = """
            public class Game
            {
                public static int Run(int v)
                {
                    if (v > 0) { return v; }
                    return -v;
                }
            }
            """;
        var plain = Transpiler.Transpile(game);
        Assert.DoesNotContain("__tcs_trace", plain);
        Assert.DoesNotContain("__tcs_trace_classes", plain);
        Assert.DoesNotContain("__tcs_at", plain);
        // path を与えても prefix が無ければ同じ出力
        var withPath = Transpiler.TranspileWithDiagnostics([game], [GamePath]);
        Assert.Equal(plain, withPath.Lua);
    }

    [Fact]
    public void Cli_Trace_RecordsLinesUnderGivenPrefix()
    {
        var dir = Directory.CreateTempSubdirectory("tcs-trace-cli");
        try
        {
            var game = Path.Combine(dir.FullName, "Game.cs");
            File.WriteAllText(game, """
                public class Game
                {
                    public static int Run() { return 7; }
                }
                """);
            var outPath = Path.Combine(dir.FullName, "out.lua");
            var r = ConsoleCapture.Run(() => Program.Main(
                [game, "-o", outPath, "--trace", dir.FullName + Path.DirectorySeparatorChar]));
            Assert.Equal(0, r.ExitCode);
            var output = TestHelper.RunLua($"""
                dofile("{outPath.Replace("\\", "/")}")
                Game.Run()
                {PrintTraceKeys}
                """).Trim();
            Assert.Equal($"{game.Replace("\\", "/")}:3", output);
        }
        finally
        {
            dir.Delete(true);
        }
    }

    // --watch --reload の batch も -o の出力と同じ prefix と入力 path で trace する
    [Fact]
    public void Watch_ReloadTracesWithInputPath()
    {
        var dir = Directory.CreateTempSubdirectory("tcs-trace-watch");
        var game = Path.Combine(dir.FullName, "Game.cs");
        var outPath = Path.Combine(dir.FullName, "out.lua");
        static string Game(int n) => $$"""
            public static class Game
            {
                public static int Score()
                {
                    int s = {{n}};
                    return s;
                }
            }
            """;
        try
        {
            File.WriteAllText(game, Game(1));
            var psi = WatchModeTests.CreateTranspilerProcess(game, "-o", outPath,
                "--entry", "Game", "--watch", "--reload",
                "--trace", dir.FullName + Path.DirectorySeparatorChar);
            using var proc = Process.Start(psi)!;
            var messages = new WatchMessageReader(proc.StandardOutput.BaseStream);
            try
            {
                WatchModeTests.WaitForFile(outPath, timeoutMs: 15000);
                var v1 = File.ReadAllText(outPath);
                Thread.Sleep(300);
                File.WriteAllText(game, Game(2));
                var chunk = messages.Next("reload_chunk", timeoutMs: 5000);
                var key = game.Replace("\\", "/");
                var output = TestHelper.RunLua($"""
                    local m = assert(load({WatchModeTests.LuaLongString(v1)}, "=out"))()
                    for k in pairs(__tcs_trace) do __tcs_trace[k] = nil end
                    assert(load({WatchModeTests.LuaLongString(chunk)}, "=chunk"))()
                    print(m.Score())
                    {PrintTraceKeys}
                    """).Trim().Split('\n');
                Assert.Equal(["2", $"{key}:5,{key}:6"], output);
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            dir.Delete(true);
        }
    }

    [Fact]
    public void Cli_Trace_RejectsSnapshot()
    {
        var r = ConsoleCapture.Run(() => Program.Main(
            ["a.cs", "--snapshot", "--entry", "A", "--trace", "a/"]));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--trace", r.Stderr);
    }

    [Fact]
    public void Cli_Trace_RequiresValue()
    {
        var r = ConsoleCapture.Run(() => Program.Main(["a.cs", "--trace"]));
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("missing value for --trace", r.Stderr);
    }
}
