using TinyCs.Vm;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// tcs VM の実行系 (doc/vm-design.md)。IL → tcs2vm → .tcsb → tcsvm。
/// AssertParity / AssertFaultParity / AssertParityLib が C と同じ stdout を要求する。
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
        byte[] bytes;
        try
        {
            bytes = SelfHostVm.Emit(sources, entryClass, exports);
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
    /// Report。plan は tcs vm --reload-from と同じ (TcsVm.VmEmitter.EmitReload)。GC stress でも同じ出力であること。</summary>
    public static (int ExitCode, string Stdout, string Stderr) RunVmReload(string[] v1, string[] v2, string cls,
        int frames = 1, IEnumerable<string>? vmArgs = null)
    {
        string[] exports = [cls + ".*"];
        var v1Bytes = SelfHostVm.Emit(v1, null, exports);
        var (v2Bytes, plan) = SelfHostVm.EmitReload(v1, v2, null, exports);
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
                if (vmArgs != null) args.AddRange(vmArgs);
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

    /// <summary>状態の保存と読み込み (doc/vm-design.md §13.4)。save を Setup → Frame×saveAt で走らせて状態を file に書き、
    /// 別の process の load が Setup の代わりにその状態を読み込んで Frame×(total - saveAt) → Report。
    /// 戻り値の Baseline は save を止めずに Frame×total → Report まで走らせた stdout。tamper は読み込む前の byte 列を変える。
    /// 読み込む側は GC stress でも同じ出力であること。</summary>
    public static (string Baseline, int ExitCode, string Stdout, string Stderr) RunVmState(string[] save, string[] load,
        string cls, int saveAt, int total, Func<byte[], byte[]>? tamper = null)
    {
        string[] exports = [cls + ".*"];
        var dir = Directory.CreateTempSubdirectory("tcsvm-state-");
        try
        {
            var p1 = Path.Combine(dir.FullName, "save.tcsb");
            var p2 = Path.Combine(dir.FullName, "load.tcsb");
            var ps = Path.Combine(dir.FullName, "state.bin");
            File.WriteAllBytes(p1, SelfHostVm.Emit(save, null, exports));
            File.WriteAllBytes(p2, SelfHostVm.Emit(load, null, exports));
            string N(int n) => n.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var baseline = Run(Start(VmExe.Value, ["--lib", cls, N(total), p1]), RunTimeout);
            Assert.True(baseline.ExitCode == 0, baseline.Stderr);
            var saved = Run(Start(VmExe.Value, ["--lib", cls, N(saveAt), "--save-state", N(saveAt), ps, p1]), RunTimeout);
            Assert.True(saved.ExitCode == 0, saved.Stderr);
            if (tamper != null) File.WriteAllBytes(ps, tamper(File.ReadAllBytes(ps)));
            (int, string, string)? first = null;
            foreach (var stress in new[] { false, true })
            {
                var args = new List<string> { "--lib", cls, N(total - saveAt), "--load-state", "0", ps };
                if (stress) args.Add("--gc-stress");
                args.Add(p2);
                var run = Run(Start(VmExe.Value, args), RunTimeout);
                var result = (run.ExitCode, run.Stdout.Replace("\r\n", "\n").TrimEnd('\n'), run.Stderr);
                if (first is { } f)
                    Assert.True(f.Item1 == result.ExitCode && f.Item2 == result.Item2,
                        $"gc stress differs:\n{f.Item2}\n---\n{result.Item2}\n{result.Stderr}");
                first = result;
            }
            return (baseline.Stdout.Replace("\r\n", "\n").TrimEnd('\n'), first!.Value.Item1, first.Value.Item2, first.Value.Item3);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>module を lib 形で走らせ、frames 回目のフレームの後に batch (VmSession / tcs2vm --watch の形) を当てる。</summary>
    public static (int ExitCode, string Stdout, string Stderr) RunVmBatch(byte[] module, byte[] batch, string cls, int frames)
    {
        var dir = Directory.CreateTempSubdirectory("tcsvm-batch-");
        try
        {
            var p1 = Path.Combine(dir.FullName, "v1.tcsb");
            var pb = Path.Combine(dir.FullName, "v2.batch");
            File.WriteAllBytes(p1, module);
            File.WriteAllBytes(pb, batch);
            var n = frames.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var run = Run(Start(VmExe.Value, ["--lib", cls, n, "--reload-batch", n, pb, p1]), RunTimeout);
            return (run.ExitCode, run.Stdout.Replace("\r\n", "\n").TrimEnd('\n'), run.Stderr);
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    public static string RunVm(string[] sources, string entryClass) =>
        RunVmProcess(sources, entryClass, []);

    /// <summary>.tcsb を作るだけで動かさない。TinyC# の診断は InvalidOperationException。</summary>
    public static byte[] EmitVm(string[] sources, string? entryClass) => SelfHostVm.Emit(sources, entryClass);

    /// <summary>終わらない program を、stdout を pipe にした tcsvm で動かす。先頭の lines 行か timeout まで読み、止めて、読めた行を返す。</summary>
    public static List<string> FirstVmLines(string source, string entryClass, int lines, TimeSpan timeout)
    {
        var dir = Directory.CreateTempSubdirectory("tcsvm-test-");
        try
        {
            var path = Path.Combine(dir.FullName, "program.tcsb");
            File.WriteAllBytes(path, SelfHostVm.Emit([source], entryClass));
            using var process = System.Diagnostics.Process.Start(Start(VmExe.Value, [path]))
                ?? throw new InvalidOperationException("cannot start tcsvm");
            var read = new List<string>();
            var reader = Task.Run(() =>
            {
                while (read.Count < lines && process.StandardOutput.ReadLine() is { } line) read.Add(line);
            });
            reader.Wait(timeout);
            process.Kill(entireProcessTree: true);
            // 止めると stdout が閉じ、reader は抜ける
            reader.Wait();
            return read;
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>lib 形: Setup → Frame×N (フレームの後で GC) → Report。</summary>
    public static string RunVmLib(string[] sources, string cls, int frames) =>
        RunVmProcess(sources, null, ["--lib", cls, frames.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            [cls + ".*"]);
}
