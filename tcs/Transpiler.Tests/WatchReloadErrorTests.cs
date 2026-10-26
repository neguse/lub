using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchReloadErrorTests
{
    // compile error の版では batch を送らず、error の行を build_failed で送る。直した次の版の
    // batch は、最後に動いた版から当たる
    [Fact]
    public void Watch_Reload_CompileErrorBetweenEdits_SendsErrorsThenTheFixedBatch()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "Game.cs");
        var outputPath = Path.Combine(tmpDir, "Game.lua");
        const string Game = """
            public static class Game
            {
                public static int OnFrame() { return 1; }
            }
            """;
        var broken = Game.Replace("return 1;", "return missing;");
        var fixedGame = Game.Replace("return 1;", "return 3;");

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
                File.WriteAllText(inputPath, broken);
                var errors = messages.Next("build_failed", timeoutMs: 10000);
                Assert.Contains("error CS0103", errors);
                Assert.Contains("'missing'", errors);
                Assert.EndsWith("\n", errors);
                Thread.Sleep(500);
                File.WriteAllText(inputPath, fixedGame);
                var chunk = messages.Next("reload_chunk", timeoutMs: 10000);
                Assert.Contains("rev 2 on 1", chunk);
                Assert.Equal("1\t3", TestHelper.RunLua($$"""
                    local m = assert(load({{WatchModeTests.LuaLongString(initial)}}, "=v1"))()
                    local before = m.OnFrame()
                    assert(load({{WatchModeTests.LuaLongString(chunk)}}, "=v2"))()
                    print(before, m.OnFrame())
                    """).Trim());
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
            Assert.Contains("Build FAILED", proc.StandardError.ReadToEnd());
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
