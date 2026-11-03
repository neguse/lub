using System.Diagnostics;
using System.Globalization;
using TinyCs.Vm;

namespace TinyCs.Tests.SpecConformance;

/// <summary>
/// TranspileAndRun の corpus を VM (doc/vm-design.md) でも走らせる。luaExpr を C# の式に翻訳できる (CorpusDifferential と
/// 同じ翻訳) 呼び出しだけ、program に `Console.WriteLine(式)` の entry を足して tcs2vm → tcsvm で走らせ、Lua の結果と比べる。
/// TCS_VM_DIFFERENTIAL=1 で有効。VM が未対応の形 (tcs2vm が止める) は TCS_VM_DIFFERENTIAL_LOG に記録して通す。
/// </summary>
internal static class VmDifferential
{
    private static readonly object Lock = new();
    private static string? _exe;

    public static bool Enabled => Environment.GetEnvironmentVariable("TCS_VM_DIFFERENTIAL") == "1";

    public static void Check(string csharpSource, string luaExpr, string luaResult)
    {
        if (!Enabled || csharpSource.Contains("Random", StringComparison.Ordinal)) return;
        var expression = CorpusDifferential.TranslateExpression(luaExpr);
        if (expression is null)
        {
            Log("skip-untranslatable", luaExpr);
            return;
        }
        var probe = csharpSource + "\npublic static class VmDiffProbe { public static void Main() { System.Console.WriteLine("
            + expression + "); } }\n";
        byte[] bytes;
        try
        {
            bytes = SelfHostVm.Emit([probe], "VmDiffProbe");
        }
        catch (InvalidOperationException e) when (e.Message.StartsWith("TinyC# diagnostics:", StringComparison.Ordinal))
        {
            Log("skip-compile", e.Message.Split('\n')[1]);
            return;
        }
        catch (VmException e)
        {
            Log("vm-unsupported", e.Message.Split('\n')[0], probe);
            return;
        }
        var dir = Directory.CreateTempSubdirectory("tcsvm-diff-");
        try
        {
            var path = Path.Combine(dir.FullName, "p.tcsb");
            File.WriteAllBytes(path, bytes);
            var (code, stdout, stderr) = Run(Exe(), path);
            var vm = stdout.Replace("\r\n", "\n").TrimEnd('\n');
            if (code != 0 || !Agree(vm, luaResult))
            {
                Log("mismatch", luaExpr, probe);
                throw new InvalidOperationException("VM differential mismatch\n" +
                    $"expr: {luaExpr}\nvm:   {vm} (exit {code}) {stderr.Split('\n')[0]}\nlua:  {luaResult}");
            }
            Log("match", luaExpr);
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private static bool Agree(string vm, string lua)
    {
        if (vm == lua) return true;
        if (vm is "True" or "False") return vm.ToLowerInvariant() == lua;
        if (vm == "" && lua == "nil") return true; // C# の null の表示は空
        return double.TryParse(vm, NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(lua, NumberStyles.Float, CultureInfo.InvariantCulture, out var b)
            && ((float)a).Equals((float)b);
    }

    private static (int, string, string) Run(string exe, string path)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, [path])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30_000)) p.Kill(true);
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>tcsvm を source から 1 回だけ build する。</summary>
    private static string Exe()
    {
        lock (Lock)
        {
            if (_exe is not null) return _exe;
            var root = Path.GetDirectoryName(TestHelper.FindProjectFile("runtime/tinysystem.lua"))!;
            root = Path.GetDirectoryName(root)!;
            var dir = Directory.CreateTempSubdirectory("tcsvm-bin-");
            var exe = Path.Combine(dir.FullName, "tcsvm");
            var args = new List<string> { "-std=c11", "-O1", "-ffp-contract=off", "-fno-fast-math" };
            args.AddRange(Directory.GetFiles(Path.Combine(root, "tcsvm", "vm"), "*.c"));
            foreach (var dep in new[] { "sort", "fmt", "math", "utf8", "model" })
                args.AddRange(Directory.GetFiles(Path.Combine(root, "runtime", dep), "*.c"));
            args.AddRange(["-o", exe, "-lm"]);
            var (code, _, err) = RunCompiler(Environment.GetEnvironmentVariable("CC") ?? "cc", args);
            if (code != 0) throw new InvalidOperationException("tcsvm build failed:\n" + err);
            return _exe = exe;
        }
    }

    private static (int, string, string) RunCompiler(string cc, List<string> args)
    {
        using var p = Process.Start(new ProcessStartInfo(cc, args) { RedirectStandardError = true, RedirectStandardOutput = true })!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, "", err);
    }

    private static void Log(string kind, string detail, string? source = null)
    {
        if (Environment.GetEnvironmentVariable("TCS_VM_DIFFERENTIAL_LOG") is not { Length: > 0 } path) return;
        lock (Lock)
        {
            var line = $"{kind}\t{detail.Replace('\n', ' ')}";
            if (source is not null)
            {
                // 調べる用に program を log の隣に置く
                var file = path + "." + Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                    System.Text.Encoding.UTF8.GetBytes(source)))[..8] + ".cs";
                File.WriteAllText(file, source);
                line += "\t" + file;
            }
            File.AppendAllText(path, line + "\n");
        }
    }
}
