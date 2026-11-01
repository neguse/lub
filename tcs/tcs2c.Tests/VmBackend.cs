using TinyCs.Vm;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// 3 本目の実行系: tcs VM (doc/vm-design.md)。IL → tcs2vm → .tcsb → tcsvm。
/// AssertParity / AssertFaultParity / AssertParityLib が Lua と同じ stdout を要求する。
/// GC stress (nursery を極小にして毎フレーム全回収) でも同じ出力であることを要求する。
/// TCSVM_PARITY=0 で外せる。
/// </summary>
internal static partial class Backends
{
    public static readonly bool VmEnabled = Environment.GetEnvironmentVariable("TCSVM_PARITY") != "0";

    private static readonly Lazy<string> VmExe = new(BuildVm);

    /// <summary>tcsvm を source から 1 回だけ build する (test の process ごと)。</summary>
    private static string BuildVm()
    {
        var compiler = CCompiler ?? throw new InvalidOperationException("no C compiler available");
        var dir = Directory.CreateTempSubdirectory("tcsvm-bin-");
        var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "tcsvm.exe" : "tcsvm");
        var args = new List<string> { "-std=c11", "-O1", "-g", "-ffp-contract=off", "-fno-fast-math" };
        args.AddRange(Directory.GetFiles(Path.Combine(ProjectRoot, "tcsvm", "vm"), "*.c"));
        foreach (var dep in new[] { "runtime/sort", "runtime/fmt", "runtime/math", "runtime/utf8", "runtime/model" })
            args.AddRange(Directory.GetFiles(Path.Combine(ProjectRoot, dep), "*.c"));
        args.AddRange(["-o", exe, "-lm"]);
        var build = Run(Start(compiler, args), CompileTimeout);
        if (build.ExitCode != 0) throw new InvalidOperationException($"tcsvm build failed:\n{build.Stderr}");
        return exe;
    }

    private static string RunVmProcess(string[] sources, string? entryClass, IEnumerable<string> vmArgs,
        IEnumerable<string>? exports = null)
    {
        var exported = IlExport.Export(sources, specializeGenerics: true);
        if (exported.Diagnostics.Length > 0)
            throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", exported.Diagnostics));
        byte[] bytes;
        try
        {
            bytes = VmEmitter.Emit(exported, entryClass, exports);
        }
        catch (VmException ex)
        {
            Dump(sources, ex.Message);
            throw new Xunit.Sdk.XunitException($"tcs2vm: {ex.Message}");
        }
        var dir = Directory.CreateTempSubdirectory("tcsvm-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "program.tcsb");
            File.WriteAllBytes(path, bytes);
            var outputs = new List<string>();
            foreach (var stress in new[] { false, true })
            {
                var args = new List<string>(vmArgs);
                if (stress) args.Add("--gc-stress");
                args.Add(path);
                var run = Run(Start(VmExe.Value, args), RunTimeout);
                var stdout = run.Stdout.Replace("\r\n", "\n").TrimEnd('\n');
                if (run.ExitCode != 0)
                {
                    Dump(sources, run.Stderr.Split('\n')[0]);
                    if (!run.Stderr.Contains("fault:"))
                        throw new Xunit.Sdk.XunitException($"tcsvm exited with {run.ExitCode}:\n{run.Stderr}");
                    throw Exited($"tcsvm{(stress ? " (gc stress)" : "")} exited with {run.ExitCode}:\n{run.Stderr}\nstdout:\n{run.Stdout}",
                        run.Stdout);
                }
                outputs.Add(stdout);
            }
            if (outputs[0] != outputs[1]) Dump(sources, "gc stress differs");
            Assert.Equal(outputs[0], outputs[1]);
            return outputs[0];
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>TCSVM_DUMP=dir なら、VM で失敗した program の source を dir に書く (調べる用)。</summary>
    private static void Dump(string[] sources, string why)
    {
        if (Environment.GetEnvironmentVariable("TCSVM_DUMP") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n//--\n", sources))))[..10];
        File.WriteAllText(Path.Combine(dir, key + ".cs"), $"// {why}\n" + string.Join("\n", sources));
    }

    /// <summary>reload (doc/object-model.md §6): v1 を lib 形で Setup → Frame×frames、後に v2 を tvm_apply で当てて
    /// Report。plan は tcs2vm --reload-from と同じ (VmEmitter.EmitReload)。GC stress でも同じ出力であること。</summary>
    public static (int ExitCode, string Stdout, string Stderr) RunVmReload(string[] v1, string[] v2, string cls,
        int frames = 1)
    {
        var old = IlExport.Export(v1, specializeGenerics: true);
        var next = IlExport.Export(v2, specializeGenerics: true);
        foreach (var e in new[] { old, next })
            if (e.Diagnostics.Length > 0)
                throw new InvalidOperationException("TinyC# diagnostics:\n" + string.Join("\n", e.Diagnostics));
        string[] exports = [cls + ".*"];
        var v1Bytes = VmEmitter.Emit(old, null, exports);
        var (v2Bytes, plan) = VmEmitter.EmitReload(old, next, null, exports);
        var dir = Directory.CreateTempSubdirectory("tcsvm-reload-");
        try
        {
            var p1 = Path.Combine(dir.FullName, "v1.tcsb");
            var p2 = Path.Combine(dir.FullName, "v2.tcsb");
            var pp = Path.Combine(dir.FullName, "v2.plan");
            File.WriteAllBytes(p1, v1Bytes);
            File.WriteAllBytes(p2, v2Bytes);
            File.WriteAllBytes(pp, plan);
            var n = frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
            (int, string, string)? first = null;
            foreach (var stress in new[] { false, true })
            {
                var args = new List<string> { "--lib", cls, n, "--reload", n, p2, pp };
                if (stress) args.Add("--gc-stress");
                args.Add(p1);
                var run = Run(Start(VmExe.Value, args), RunTimeout);
                var result = (run.ExitCode, run.Stdout.Replace("\r\n", "\n").TrimEnd('\n'), run.Stderr);
                if (first is { } f)
                    Assert.True(f.Item1 == result.ExitCode && f.Item2 == result.Item2,
                        $"gc stress differs:\n{f.Item2}\n---\n{result.Item2}\n{result.Stderr}");
                first = result;
            }
            return first!.Value;
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    public static string RunVm(string[] sources, string entryClass) =>
        RunVmProcess(sources, entryClass, []);

    /// <summary>lib 形: Setup → Frame×N (フレームの後で GC) → Report。</summary>
    public static string RunVmLib(string[] sources, string cls, int frames) =>
        RunVmProcess(sources, null, ["--lib", cls, frames.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            [cls + ".*"]);
}
