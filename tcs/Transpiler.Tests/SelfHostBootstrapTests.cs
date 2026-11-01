using System.Diagnostics;

namespace TinyCs.Tests;

/// <summary>
/// tcs (SelfHost/Compiler と SelfHost/Tool) の bootstrap の検査。
///   stage1: .NET の tcs (同じソースを .NET で動かしたもの) が tcs を Lua にしたもの
///   stage2: stage1 が lua32 の上 (SelfHost/Host/tcsc.lua) で同じソースを Lua にしたもの
///   stage3: stage2 が同じソースを Lua にしたもの
/// stage2 と stage3 が byte で一致すること (固定点) と、stage1 と stage2 が一致すること
/// (同じソースを .NET と Lua の経路で動かして決定的であること) を確かめる。C の経路は SelfHost/build.sh が見る。
/// </summary>
public class SelfHostBootstrapTests
{
    private static readonly TimeSpan StageTimeout = TimeSpan.FromSeconds(180);

    [Fact]
    public void Bootstrap_ReachesFixedPoint_AndMatchesDotnetRun()
    {
        var root = Path.GetDirectoryName(TestHelper.FindProjectFile("tcs.slnx"))!;
        var hostStub = Path.Combine(root, "SelfHost", "Tool", "TcsHost.cs");
        var paths = Directory.GetFiles(Path.Combine(root, "SelfHost", "Compiler"), "*.cs")
            .Concat(Directory.GetFiles(Path.Combine(root, "SelfHost", "Tool"), "*.cs").Where(p => p != hostStub))
            .ToArray();
        var args = new List<string> { "--ref", hostStub };
        args.AddRange(paths);
        var work = Directory.CreateTempSubdirectory("tcs-selfhost-");
        try
        {
            // tcs と一緒に配るファイル (Lua の host は stage の Lua の dir から読む)
            Directory.CreateDirectory(Path.Combine(work.FullName, "runtime"));
            Directory.CreateDirectory(Path.Combine(work.FullName, "lib", "TinySystem"));
            foreach (var name in new[] { "tinysystem.lua", "module_registry.lua" })
                File.Copy(Path.Combine(root, "runtime", name), Path.Combine(work.FullName, "runtime", name));
            foreach (var name in new[] { "Linq.cs", "ValueTuple.cs" })
                File.Copy(Path.Combine(root, "TinySystem", name), Path.Combine(work.FullName, "lib", "TinySystem", name));

            var stage1 = Path.Combine(work.FullName, "stage1.lua");
            var (exitCode, _, stderr) = ConsoleCapture.Run(() => Program.Main([.. args, "-o", stage1]));
            Assert.True(exitCode == 0, stderr);

            var stage2 = Path.Combine(work.FullName, "stage2.lua");
            RunStage(root, stage1, stage2, args);
            var stage3 = Path.Combine(work.FullName, "stage3.lua");
            RunStage(root, stage2, stage3, args);

            var stage2Text = File.ReadAllText(stage2);
            Assert.True(stage2Text == File.ReadAllText(stage3), "stage2 != stage3 (not a fixed point)");
            Assert.True(File.ReadAllText(stage1) == stage2Text, "stage1 (.NET) != stage2 (Lua)");
        }
        catch
        {
            // 調べられるように中間の出力を残す
            Console.Error.WriteLine("bootstrap outputs kept in " + work.FullName);
            throw;
        }
        work.Delete(true);
    }

    private static void RunStage(string root, string tool, string output, List<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = TestHelper.LuaExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment.Remove("TCS_HOME");
        psi.ArgumentList.Add(Path.Combine(root, "SelfHost", "Host", "tcsc.lua"));
        psi.ArgumentList.Add(tool);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add("-o");
        psi.ArgumentList.Add(output);
        var result = TestProcessRunner.Run(psi, StageTimeout);
        Assert.True(result.ExitCode == 0, $"{Path.GetFileName(tool)} failed:\n{result.Stderr}");
    }
}
