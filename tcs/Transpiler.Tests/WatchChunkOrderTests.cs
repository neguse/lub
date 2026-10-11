using System.Diagnostics;

namespace TinyCs.Tests;

public class WatchChunkOrderTests
{
    // ホスト (lub --serve) は接続時に -o の出力を読む。chunk の frame が届いた時点で
    // その build の出力と source map は書き終えていなければならない: そうでないと
    // frame と書き込みの間につないだホストは古い出力を読み、chunk を取りこぼす。
    // 書き終えた出力で起動したホストには、その chunk は何もしない
    [Fact]
    public void Watch_ReloadChunks_OutputIsWrittenBeforeChunkFrame()
    {
        var tmpDir = Path.Combine(Path.GetTempPath(), $"tcs_watch_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);

        var inputPath = Path.Combine(tmpDir, "Game.cs");
        var outputPath = Path.Combine(tmpDir, "Game.lua");
        var mapPath = outputPath + ".map";
        static string Game(int n) => $$"""
            public static class Game
            {
                static int frames = 0;
                public static int OnFrame()
                {
                    frames = frames + 1;
                    return frames + {{n}};
                }
            }
            """;

        try
        {
            File.WriteAllText(inputPath, Game(0));
            var psi = WatchModeTests.CreateTranspilerProcess(inputPath, "-o", outputPath,
                "--entry", "Game", "--sourcemap", "--watch", "--reload-chunks");

            using var proc = Process.Start(psi)!;
            var messages = new WatchMessageReader(proc.StandardOutput.BaseStream);
            try
            {
                WatchModeTests.WaitForFile(outputPath, timeoutMs: 15000);
                for (var build = 2; build <= 6; build++)
                {
                    Thread.Sleep(300);
                    File.WriteAllText(inputPath, Game(build * 100));
                    var chunk = messages.Next("reload_chunk", timeoutMs: 5000);
                    var output = File.ReadAllText(outputPath);
                    var map = File.ReadAllText(mapPath);

                    Assert.StartsWith($"__tcs_build = {build}\n", output);
                    Assert.Equal("1\t" + (build * 100 + 2), TestHelper.RunLua($$"""
                        local m = assert(load({{WatchModeTests.LuaLongString(output)}}, "=out"))()
                        local first = m.OnFrame()
                        assert(load({{WatchModeTests.LuaLongString(chunk)}}, "=chunk"))()
                        print(first - {{build * 100}}, m.OnFrame())
                        """).Trim());

                    // 次の編集までに書き足されるものは無い
                    Thread.Sleep(300);
                    Assert.Equal(output, File.ReadAllText(outputPath));
                    Assert.Equal(map, File.ReadAllText(mapPath));
                }
            }
            finally
            {
                proc.Kill(entireProcessTree: true);
                proc.WaitForExit(3000);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, true); } catch { }
        }
    }
}
