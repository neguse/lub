using System.Diagnostics;

namespace TinyCs.Tests;

// --watch --reload: -o に毎回全部の module snapshot を書き、動いている VM へ変わった
// module だけの batch (基準の revision と instance の移行付き) を reload_chunk で送る
public class ModuleWatchTests
{
    private const string Game = """
        public static class Game
        {
            static int frames;
            static Enemy? boss;
            public static int OnFrame()
            {
                boss ??= new Enemy();
                frames = frames + 1;
                return frames * 100 + Lib.Bonus();
            }
            public static int Probe() { return boss!.Hp; }
        }
        """;

    private const string Enemy = """
        public class Enemy
        {
            public int Hp = 3;
        }
        """;

    private const string Lib = """
        public static class Lib
        {
            public static int Bonus() { return 1; }
        }
        """;

    private sealed class Watch : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), $"tcs_mwatch_{Guid.NewGuid():N}");
        public readonly string Output;
        private readonly Process _proc;
        public readonly WatchMessageReader Messages;

        public Watch(params string[] extraArgs)
        {
            Directory.CreateDirectory(Dir);
            Output = Path.Combine(Dir, "Game.lua");
            Write("Game.cs", Game);
            Write("Enemy.cs", Enemy);
            Write("Lib.cs", Lib);
            var psi = WatchModeTests.CreateTranspilerProcess(
                [Path.Combine(Dir, "Game.cs"), Path.Combine(Dir, "Enemy.cs"), Path.Combine(Dir, "Lib.cs"),
                    "-o", Output, "--entry", "Game", "--watch", "--reload", .. extraArgs]);
            _proc = Process.Start(psi)!;
            Messages = new WatchMessageReader(_proc.StandardOutput.BaseStream);
            WatchModeTests.WaitForFile(Output, timeoutMs: 15000);
            Thread.Sleep(500);
        }

        public void Write(string name, string text) => File.WriteAllText(Path.Combine(Dir, name), text);

        public string Log()
        {
            _proc.Kill(entireProcessTree: true);
            _proc.WaitForExit(3000);
            return _proc.StandardError.ReadToEnd();
        }

        public void Dispose()
        {
            if (!_proc.HasExited)
            {
                _proc.Kill(entireProcessTree: true);
                _proc.WaitForExit(3000);
            }
            try { Directory.Delete(Dir, true); } catch { }
        }
    }

    private static string Load(string lua, string name) =>
        $"assert(load({WatchModeTests.LuaLongString(lua)}, \"={name}\"))()";

    [Fact]
    public void BodyEdit_SendsBatchOfChangedModule_KeepsState()
    {
        using var w = new Watch();
        var snapshot = File.ReadAllText(w.Output);
        w.Write("Lib.cs", Lib.Replace("return 1;", "return 7;"));
        var batch = w.Messages.Next("reload_chunk", timeoutMs: 10000);

        Assert.Contains("Lib.cs", batch);
        Assert.DoesNotContain("Enemy.cs\",", batch);
        Assert.Equal("101\t201\t307", TestHelper.RunLua($"""
            local g = {Load(snapshot, "snapshot")}
            local a, b = g.OnFrame(), g.OnFrame()
            {Load(batch, "batch")}
            print(a, b, g.OnFrame())
            """).Trim());
    }

    [Fact]
    public void FieldAdd_MigratesLiveInstance()
    {
        using var w = new Watch();
        var snapshot = File.ReadAllText(w.Output);
        w.Write("Enemy.cs", Enemy.Replace("public int Hp = 3;", "public int Hp = 3;\n    public int Armor = 5;"));
        w.Write("Game.cs", Game.Replace("return boss!.Hp;", "return boss!.Hp * 10 + boss!.Armor;"));
        var batch = w.Messages.Next("reload_chunk", timeoutMs: 10000);

        Assert.Equal("3\t35", TestHelper.RunLua($"""
            local g = {Load(snapshot, "snapshot")}
            g.OnFrame()
            local before = g.Probe()
            {Load(batch, "batch")}
            print(before, g.Probe())
            """).Trim());
    }

    [Fact]
    public void StaticInitializerChange_RequestsRestart_ThenBatchesApplyToRestartedVm()
    {
        using var w = new Watch();
        var initial = File.ReadAllText(w.Output);
        var changedInit = Game.Replace("static int frames;", "static int frames = 50;");
        w.Write("Game.cs", changedInit);
        Assert.Equal("static initializer changed: Game.frames\n",
            w.Messages.Next("restart_required", timeoutMs: 10000).Split(": ", 2)[1]);

        var restarted = File.ReadAllText(w.Output);
        Thread.Sleep(500);
        w.Write("Lib.cs", Lib.Replace("return 1;", "return 2;"));
        var batch = w.Messages.Next("reload_chunk", timeoutMs: 10000);
        Assert.Equal("5101\t5202", TestHelper.RunLua($"""
            local g = {Load(restarted, "restarted")}
            local before = g.OnFrame()
            {Load(batch, "batch")}
            print(before, g.OnFrame())
            """).Trim());

        // 起動し直していない VM には当たらず、状態も変えない
        Assert.Equal("false\ttrue\t201", TestHelper.RunLua($"""
            local g = {Load(initial, "initial")}
            g.OnFrame()
            local ok, err = pcall(assert(load({WatchModeTests.LuaLongString(batch)}, "=batch")))
            print(ok, err:find("restart the host", 1, true) ~= nil, g.OnFrame())
            """).Trim());
    }

    [Fact]
    public void CompileError_SendsBuildFailed_ThenFixedBatchFromRunningRevision()
    {
        using var w = new Watch();
        var snapshot = File.ReadAllText(w.Output);
        w.Write("Lib.cs", Lib.Replace("return 1;", "return 3"));
        Assert.Contains("error CS", w.Messages.Next("build_failed", timeoutMs: 10000));
        Thread.Sleep(500);
        w.Write("Lib.cs", Lib.Replace("return 1;", "return 3;"));
        var batch = w.Messages.Next("reload_chunk", timeoutMs: 10000);
        Assert.Equal("101\t203", TestHelper.RunLua($"""
            local g = {Load(snapshot, "snapshot")}
            local before = g.OnFrame()
            {Load(batch, "batch")}
            print(before, g.OnFrame())
            """).Trim());
    }

    [Fact]
    public void SeveralFilesInOneSave_SendOneBatch()
    {
        using var w = new Watch();
        var snapshot = File.ReadAllText(w.Output);
        w.Write("Lib.cs", Lib.Replace("return 1;", "return 4;"));
        w.Write("Game.cs", Game.Replace("frames * 100", "frames * 1000"));
        var batch = w.Messages.Next("reload_chunk", timeoutMs: 10000);
        Assert.Equal("101\t2004", TestHelper.RunLua($"""
            local g = {Load(snapshot, "snapshot")}
            local before = g.OnFrame()
            {Load(batch, "batch")}
            print(before, g.OnFrame())
            """).Trim());
    }

    [Fact]
    public void Reload_RequiresWatch()
    {
        var dir = Directory.CreateTempSubdirectory("tcs-mwatch-args");
        try
        {
            var game = Path.Combine(dir.FullName, "Game.cs");
            File.WriteAllText(game, Game.Replace("Lib.Bonus()", "1").Replace("static Enemy? boss;", "").Replace("boss ??= new Enemy();", "").Replace("return boss!.Hp;", "return 0;"));
            var psi = WatchModeTests.CreateTranspilerProcess(game,
                "-o", Path.Combine(dir.FullName, "Game.lua"), "--entry", "Game", "--reload");
            using var proc = Process.Start(psi)!;
            proc.WaitForExit(30000);
            Assert.Equal(1, proc.ExitCode);
            Assert.Contains("--reload requires --watch", proc.StandardError.ReadToEnd());
        }
        finally
        {
            dir.Delete(true);
        }
    }
}
