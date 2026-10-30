using System.Diagnostics;

namespace TinyCs.Tests;

// runtime の Lua は生成物に埋め込まれてホストで実行されるので、バイナリの隣からだけ読む。
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

        // _root は作業ディレクトリとバイナリの両方の祖先
        var planted = Directory.CreateDirectory(Path.Combine(_root, "runtime")).FullName;
        foreach (var name in new[] { "tinysystem.lua", "module_registry.lua" })
            File.WriteAllText(Path.Combine(planted, name), $"{Marker}()\n");

        var built = Path.GetDirectoryName(typeof(Program).Assembly.Location)!;
        foreach (var file in Directory.GetFiles(built))
            if (Path.GetExtension(file) == ".dll" || Path.GetFileName(file).StartsWith("Transpiler."))
                File.Copy(file, Path.Combine(_bin, Path.GetFileName(file)));
        Directory.CreateDirectory(Path.Combine(_bin, "runtime"));
        foreach (var file in Directory.GetFiles(Path.Combine(built, "runtime")))
            File.Copy(file, Path.Combine(_bin, "runtime", Path.GetFileName(file)));

        File.WriteAllText(Path.Combine(_work, "Game.cs"), """
            public static class Game
            {
                public static int OnFrame() { return 1; }
            }
            """);
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

    [Theory]
    [InlineData("--snapshot")]
    [InlineData("--entry")]
    public void CopiedBinary_UsesTheRuntimeNextToIt(string mode)
    {
        var output = Path.Combine(_work, "game.lua");
        var (exitCode, stderr) = mode == "--snapshot"
            ? RunTcs("--snapshot", "--entry", "Game", "Game.cs", "-o", output)
            : RunTcs("--entry", "Game", "Game.cs", "-o", output);

        Assert.True(exitCode == 0, stderr);
        Assert.DoesNotContain(Marker, File.ReadAllText(output));
    }

    [Theory]
    [InlineData("--snapshot")]
    [InlineData("--entry")]
    public void BinaryWithoutRuntime_FailsInsteadOfSearching(string mode)
    {
        Directory.Delete(Path.Combine(_bin, "runtime"), true);

        var output = Path.Combine(_work, "game.lua");
        var (exitCode, stderr) = mode == "--snapshot"
            ? RunTcs("--snapshot", "--entry", "Game", "Game.cs", "-o", output)
            : RunTcs("--entry", "Game", "Game.cs", "-o", output);

        Assert.NotEqual(0, exitCode);
        Assert.Contains("runtime file not found", stderr);
        Assert.False(File.Exists(output));
    }
}
