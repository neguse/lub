using System.Diagnostics;

namespace TinyCs.Tests;

/// <summary>
/// セルフホストの compiler (SelfHost/Compiler) の bootstrap の検査。
///   stage1: 参照実装が SelfHost/Compiler を Lua にしたもの
///   stage2: stage1 が lua32 の上で同じソースを Lua にしたもの
///   stage3: stage2 が同じソースを Lua にしたもの
/// stage2 と stage3 が byte で一致すること (固定点) と、同じ compiler を .NET で動かした出力が
/// stage2 と一致すること (同じソースを 3 つの経路で動かして決定的であること) を確かめる。
/// </summary>
public class SelfHostBootstrapTests
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(120);

    [Fact]
    public void Bootstrap_ReachesFixedPoint_AndMatchesDotnetRun()
    {
        var root = Path.GetDirectoryName(TestHelper.FindProjectFile("tcs.slnx"))!;
        var paths = Directory.GetFiles(Path.Combine(root, "SelfHost", "Compiler"), "*.cs")
            .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var sources = paths.Select(File.ReadAllText).ToArray();
        var work = Directory.CreateTempSubdirectory("tcs-selfhost-");
        try
        {
            // 埋め込みの runtime (compiler の出力は変換結果の見出しと helper から始まるので、runtime だけを前に付ける)
            var prelude = LuaRuntime.EmbedRuntime("").Lua;
            var preludePath = Path.Combine(work.FullName, "prelude.lua");
            File.WriteAllText(preludePath, prelude);

            var reference = Transpiler.TranspileWithDiagnostics(sources, paths, null, "Compiler");
            Assert.True(reference.Success, string.Join("\n", reference.Errors));
            var stage1 = Path.Combine(work.FullName, "stage1.lua");
            File.WriteAllText(stage1, LuaRuntime.EmbedRuntime(reference.Lua).Lua);

            var stage2 = Path.Combine(work.FullName, "stage2.lua");
            RunStage(root, stage1, preludePath, stage2, paths);
            var stage3 = Path.Combine(work.FullName, "stage3.lua");
            RunStage(root, stage2, preludePath, stage3, paths);

            var stage2Text = File.ReadAllText(stage2);
            Assert.True(stage2Text == File.ReadAllText(stage3), "stage2 != stage3 (not a fixed point)");
            // 参照実装とセルフホストの compiler は同じ入力に同じ Lua を出す
            Assert.True(File.ReadAllText(stage1) == stage2Text, "stage1 (reference) != stage2 (self-hosted)");

            var dotnet = TcsSelf.Compiler.Compile(paths.ToList(), sources.ToList(), [], [], "Compiler");
            Assert.True(dotnet.Ok, string.Join("\n", dotnet.Diagnostics));
            Assert.True(prelude + dotnet.Lua == stage2Text, "the .NET run of the self-hosted compiler differs from stage2");
        }
        finally
        {
            work.Delete(true);
        }
    }

    private static void RunStage(string root, string compiler, string prelude, string output, string[] sources)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestHelper.LuaExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.ArgumentList.Add(Path.Combine(root, "SelfHost", "Host", "tcsc.lua"));
        psi.ArgumentList.Add(compiler);
        psi.ArgumentList.Add(prelude);
        psi.ArgumentList.Add("--entry");
        psi.ArgumentList.Add("Compiler");
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);
        foreach (var s in sources) psi.ArgumentList.Add(s);
        var result = TestProcessRunner.Run(psi, StageTimeout);
        Assert.True(result.ExitCode == 0, $"{Path.GetFileName(compiler)} failed:\n{result.Stderr}");
    }
}
