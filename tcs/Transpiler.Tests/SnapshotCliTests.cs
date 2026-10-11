using TinyCs;

namespace TinyCs.Tests;

// CLI `--snapshot` (prebuilt bridge snapshot 出力) の受入テスト。
public class SnapshotCliTests
{
    [Fact]
    public void SnapshotRequiresEntry()
    {
        var r = RunCli("a.cs", "--snapshot");
        Assert.Equal(1, r.ExitCode);
        Assert.Contains("--snapshot requires --entry", r.Stderr);
    }

    [Fact]
    public void SnapshotEmitsRunnableBridgeSnapshot()
    {
        var dir = Directory.CreateTempSubdirectory("tcs-snap-cli");
        try
        {
            var game = Path.Combine(dir.FullName, "Game.cs");
            File.WriteAllText(game, """
                public class Game
                {
                    public static int Run() { return Lib.Twice(21); }
                }
                """);
            var lib = Path.Combine(dir.FullName, "lib/Lib.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(lib)!);
            File.WriteAllText(lib, """
                public class Lib
                {
                    public static int Twice(int x) { return x * 2; }
                }
                """);
            var outPath = Path.Combine(dir.FullName, "snap.lua");
            var r = RunCli(game, lib, "--entry", "Game", "--snapshot",
                "-o", outPath);
            Assert.Equal(0, r.ExitCode);

            var lua = File.ReadAllText(outPath);
            // module ID は与えた path そのまま (playground との一致は呼び側の責任)
            Assert.Contains(game.Replace("\\", "/"), lua);
            var output = TestHelper.RunLua($"""
                local w = dofile("{outPath.Replace("\\", "/")}")
                print(w.Run())
                """).Trim().Split('\n');
            // 1 行目は commit ACK (@@tcs_commit ...)、最後が Run() の結果
            Assert.StartsWith("@@tcs_commit", output[0]);
            Assert.Equal("42", output[^1].Trim());
        }
        finally
        {
            dir.Delete(true);
        }
    }

    private static (int ExitCode, string Stdout, string Stderr) RunCli(
        params string[] args) =>
        ConsoleCapture.Run(() => Program.Main(args));
}
