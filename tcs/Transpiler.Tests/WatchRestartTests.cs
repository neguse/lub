using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchRestartTests
{
    // 移行できない編集 (static の initializer 変更) では batch を送らず、出力を書いてから
    // restart の理由を標準出力と標準エラーへ出す。ホストが出力を読み直して再起動したら、
    // 次の編集の batch はその revision へ当たる (再起動していない VM には当たらない)
    [Fact]
    public void Watch_Reload_StaticInitializerChange_RequestsRestart()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "Game.cs");
        var outputPath = Path.Combine(tmpDir, "Game.lua");
        const string Game = """
            public static class Game
            {
                static int x = 1;
                public static int OnFrame() { return x; }
            }
            """;
        var changedInit = Game.Replace("x = 1;", "x = 2;");
        var changedBody = changedInit.Replace("return x;", "return x + 10;");

        try
        {
            File.WriteAllText(inputPath, Game);
            var psi = WatchModeTests.CreateTranspilerProcess(inputPath,
                "-o", outputPath, "--entry", "Game", "--watch", "--reload");

            using var proc = Process.Start(psi)!;
            var messages = new WatchMessageReader(proc.StandardOutput.BaseStream);
            try
            {
                WatchModeTests.WaitForFile(outputPath, timeoutMs: 15000);
                var initial = File.ReadAllText(outputPath);

                Thread.Sleep(500);
                File.WriteAllText(inputPath, changedInit);
                var reasons = messages.Next("restart_required", timeoutMs: 5000);
                Assert.EndsWith(": static initializer changed: Game.x\n", reasons);

                // ホストの再起動: 書き終えた出力を新しい VM で読む
                var restarted = File.ReadAllText(outputPath);
                Thread.Sleep(500);
                File.WriteAllText(inputPath, changedBody);
                var chunk = messages.Next("reload_chunk", timeoutMs: 5000);
                Assert.Equal("2\t12", TestHelper.RunLua($$"""
                    local m = assert(load({{WatchModeTests.LuaLongString(restarted)}}, "=v2"))()
                    local before = m.OnFrame()
                    assert(load({{WatchModeTests.LuaLongString(chunk)}}, "=v3"))()
                    print(before, m.OnFrame())
                    """).Trim());

                // 起動し直していない VM には当たらず、状態も変えない
                Assert.Equal("false\ttrue\t1", TestHelper.RunLua($$"""
                    local m = assert(load({{WatchModeTests.LuaLongString(initial)}}, "=v1"))()
                    local ok, err = pcall(assert(load({{WatchModeTests.LuaLongString(chunk)}}, "=v3")))
                    print(ok, err:find("restart the host", 1, true) ~= nil, m.OnFrame())
                    """).Trim());
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            var log = proc.StandardError.ReadToEnd();
            Assert.Contains("Restart required", log);
            Assert.Contains("static initializer changed: Game.x", log);
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
