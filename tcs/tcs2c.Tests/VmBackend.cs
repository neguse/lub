using TinyCs.Vm;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// tcs VM execution backend (docs/tcs/vm-design.md). IL → tcs2vm → .tcsb → tcsvm.
/// AssertParity / AssertFaultParity / AssertParityLib require the same stdout as C.
/// Also requires the same output under GC stress (a tiny nursery and a full collection every frame).
/// Can be turned off with TCSVM_PARITY=0.
/// </summary>
internal static partial class Backends
{
    public static readonly bool VmEnabled = Environment.GetEnvironmentVariable("TCSVM_PARITY") != "0";

    private static readonly Lazy<string> VmExe = new(BuildVm);

    /// <summary>Builds tcsvm from source only once (per test process).</summary>
    private static string BuildVm()
    {
        var compiler = CCompiler ?? throw new InvalidOperationException("no C compiler available");
        var dir = Directory.CreateTempSubdirectory("tcsvm-bin-");
        var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "tcsvm.exe" : "tcsvm");
        var args = new List<string> { "-std=c11", "-O1", "-g", "-ffp-contract=off", "-fno-fast-math" };
        args.AddRange(Directory.GetFiles(Path.Combine(ProjectRoot, "..", "src", "tcs-vm"), "*.c"));
        foreach (var dep in new[] { "sort", "fmt", "math", "utf8", "model" })
            args.AddRange(Directory.GetFiles(Path.Combine(ProjectRoot, "..", "src", "tcs-runtime", dep), "*.c"));
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

    /// <summary>If TCSVM_DUMP=dir, writes the source of programs that failed on the VM to dir (for investigation).</summary>
    private static void Dump(string[] sources, string why)
    {
        if (Environment.GetEnvironmentVariable("TCSVM_DUMP") is not { Length: > 0 } dir) return;
        Directory.CreateDirectory(dir);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(string.Join("\n//--\n", sources))))[..10];
        File.WriteAllText(Path.Combine(dir, key + ".cs"), $"// {why}\n" + string.Join("\n", sources));
    }

    /// <summary>reload (docs/tcs/object-model.md §6): runs v1 in lib form as Setup → Frame×frames, then applies v2 with tvm_apply and runs
    /// Report. The plan is the same as tcs vm --reload-from (TcsVm.VmEmitter.EmitReload). The output must be the same under GC stress.</summary>
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

    /// <summary>Saving and loading state (docs/tcs/vm-design.md §13.4). Runs save as Setup → Frame×saveAt and writes the state to a file,
    /// then a load in another process reads that state instead of running Setup and runs Frame×(total - saveAt) → Report.
    /// The returned Baseline is the stdout of running save without stopping through Frame×total → Report. tamper changes the bytes before loading.
    /// The loading side must give the same output under GC stress.</summary>
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

    /// <summary>Runs a module in lib form and applies a batch (the VmSession / tcs2vm --watch form) after the frames-th frame.</summary>
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

    /// <summary>Only creates the .tcsb without running it. TinyC# diagnostics become InvalidOperationException.</summary>
    public static byte[] EmitVm(string[] sources, string? entryClass) => SelfHostVm.Emit(sources, entryClass);

    /// <summary>Runs a program that does not terminate on tcsvm with stdout piped. Reads up to the first lines lines or until timeout, stops it, and returns the lines read.</summary>
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
            // Stopping it closes stdout, and the reader exits
            reader.Wait();
            return read;
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>lib form: Setup → Frame×N (GC after each frame) → Report.</summary>
    public static string RunVmLib(string[] sources, string cls, int frames) =>
        RunVmProcess(sources, null, ["--lib", cls, frames.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            [cls + ".*"]);
}
