namespace TinyCs.Tests;

// T160: CLAUDE.md のファイルサイズ規約 (600 行警告 / 800 行禁止) の恒常ゲート。
// run-tests.sh / run-tests.ps1 / CI はいずれも dotnet test を通るため、
// テストとして実装することで判定の二重実装を避ける。
public class FileSizeGateTests
{
    private const int WarnLines = 600;
    private const int ErrorLines = 800;

    private static (bool Warn, bool Error) Classify(int lines) =>
        (lines > WarnLines, lines > ErrorLines);

    [Theory]
    [InlineData(600, false, false)]
    [InlineData(601, true, false)]
    [InlineData(800, true, false)]
    [InlineData(801, true, true)]
    public void Classify_WarnAndErrorBoundaries(int lines, bool warn, bool error)
    {
        Assert.Equal((warn, error), Classify(lines));
    }

    [Fact]
    public void TrackedCsharpSources_StayUnderErrorLimit()
    {
        var root = Path.GetDirectoryName(
            TestHelper.FindProjectFile("run-tests.sh"))!;
        var excluded = new[] { "/bin/", "/obj/", "/deps/", "/.git/" };

        var oversized = new List<string>();
        foreach (var path in Directory.EnumerateFiles(root, "*.cs",
                     SearchOption.AllDirectories))
        {
            var normalized = path.Replace('\\', '/');
            if (excluded.Any(normalized.Contains)) continue;

            var lines = File.ReadLines(path).Count();
            var (warn, error) = Classify(lines);
            var relative = Path.GetRelativePath(root, path);
            if (error)
                oversized.Add($"{relative}: {lines} lines (limit {ErrorLines})");
            else if (warn)
                Console.WriteLine(
                    $"[file-size warning] {relative}: {lines} lines (> {WarnLines})");
        }

        Assert.True(oversized.Count == 0,
            "file size limit exceeded:\n" + string.Join("\n", oversized));
    }
}
