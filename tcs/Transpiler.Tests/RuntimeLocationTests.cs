using System.Diagnostics;

namespace TinyCs.Tests;

// tcs c が生成 C に埋め込む runtime の C は、バイナリの隣 (runtime/) からだけ読む。
// 作業ディレクトリとその祖先、バイナリの祖先に置かれたものは読まない
public class RuntimeLocationTests : IDisposable
{
    private const string Marker = "planted_runtime_marker";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), $"tcs_runtime_{Guid.NewGuid():N}");
    private readonly string _bin;
    private readonly string _work;

    public RuntimeLocationTests()
    {
        _bin = Directory.CreateDirectory(Path.Combine(_root, "bin")).FullName;
        _work = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;

        var built = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        foreach (var file in Directory.GetFiles(built))
            if (Path.GetExtension(file) == ".dll" || Path.GetFileName(file).StartsWith("Transpiler."))
                File.Copy(file, Path.Combine(_bin, Path.GetFileName(file)));
        CopyTree(Path.Combine(built, "runtime"), Path.Combine(_bin, "runtime"));

        // _root は作業ディレクトリとバイナリの両方の祖先。同じ名前の runtime のファイルを marker にして置く
        foreach (var file in Directory.GetFiles(Path.Combine(_bin, "runtime"), "*", SearchOption.AllDirectories))
        {
            var planted = Path.Combine(_root, Path.GetRelativePath(_bin, file));
            Directory.CreateDirectory(Path.GetDirectoryName(planted)!);
            File.WriteAllText(planted, $"{Marker}\n");
        }

        File.WriteAllText(Path.Combine(_work, "Game.cs"), """
            public static class Game
            {
                public static void Main() { System.Console.WriteLine(1); }
            }
            """);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
            CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private (int ExitCode, string Stderr) RunTcs(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "dotnet",
            WorkingDirectory = _work,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add(Path.Combine(_bin, "Transpiler.dll"));
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var proc = Process.Start(startInfo)!;
        var stdout = proc.StandardOutput.ReadToEndAsync();
        var stderr = proc.StandardError.ReadToEndAsync();
        proc.WaitForExit();
        stdout.Wait();
        return (proc.ExitCode, stderr.Result);
    }

    [Fact]
    public void CopiedBinary_UsesTheRuntimeNextToIt()
    {
        var output = Path.Combine(_work, "game.c");
        var (exitCode, stderr) = RunTcs("c", "--entry", "Game", "Game.cs", "-o", output);

        Assert.True(exitCode == 0, stderr);
        Assert.DoesNotContain(Marker, File.ReadAllText(output));
    }

    [Fact]
    public void BinaryWithoutRuntime_FailsInsteadOfSearching()
    {
        Directory.Delete(Path.Combine(_bin, "runtime"), true);

        var output = Path.Combine(_work, "game.c");
        var (exitCode, stderr) = RunTcs("c", "--entry", "Game", "Game.cs", "-o", output);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("runtime file not found", stderr);
        Assert.False(File.Exists(output));
    }
}
