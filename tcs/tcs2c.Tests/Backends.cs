using System.Diagnostics;
using TinyCs;
using TinyCs.Tcs2c;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// 2 backend の実行ハーネス。同じ TinyC# source を tcs2c→C→gcc と
/// tcs→Lua→lua32 で実行し、stdout を突き合わせる。GC はフレーム境界
/// (lib 出荷形の tcs_lib_gc) でだけ走るので、GC の意味論は lib 形で
/// Setup / Frame×N / Report を回す AssertParityLib で検証する。C 側は
/// GC stress (-DTCS_GC_STRESS=1: 毎境界で旧世代 full GC + 256 byte の
/// nursery chunk) でも同じ出力であることを要求する — root / ライトバリア
/// の漏れは、生きている object が回収 / 上書きされて出力が変わる形で現れる。
/// </summary>
internal static class Backends
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);
    public static readonly string ProjectRoot = FindProjectRoot();
    public static readonly string? CCompiler = FindCCompiler();
    public static readonly string LuaPath = Path.Combine(ProjectRoot, "deps", "lua",
        OperatingSystem.IsWindows() ? "lua32.exe" : "lua32");

    public static readonly string[] StrictFlags =
        ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard"];

    private static string FindProjectRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            if (File.Exists(Path.Combine(dir, "tcs.slnx"))) return dir;
            var parent = Path.GetDirectoryName(dir);
            if (parent == dir) break;
            dir = parent;
        }
        return Environment.CurrentDirectory;
    }

    private static string? FindCCompiler()
    {
        var configured = Environment.GetEnvironmentVariable("CC");
        foreach (var candidate in new[] { configured, "gcc", "cc", "clang" })
        {
            if (string.IsNullOrEmpty(candidate)) continue;
            try
            {
                var psi = Start(candidate, ["--version"]);
                var result = Run(psi, TimeSpan.FromSeconds(10));
                if (result.ExitCode == 0) return candidate;
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                or InvalidOperationException)
            {
            }
        }
        return null;
    }

    public static string Sample(string relative) =>
        File.ReadAllText(Path.Combine(ProjectRoot, relative));

    /// <summary>C source を生成する (emit だけ、compile なし)。</summary>
    public static string EmitC(string[] sources, string? entryClass,
        bool lib = false, bool digestF32 = false)
    {
        var exported = IlExport.Export(sources);
        if (exported.Diagnostics.Length > 0)
            throw new InvalidOperationException("TinyC# diagnostics:\n" +
                string.Join("\n", exported.Diagnostics));
        return new CEmitter(exported, digestF32).Emit(entryClass, lib);
    }

    /// <summary>tcs2c → C compiler → 実行 (main 形: Main 全体が 1 フレームで
    /// GC は走らない)。</summary>
    public static string RunC(string[] sources, string? entryClass,
        bool stress = false, IEnumerable<string>? extraFlags = null)
    {
        var c = EmitC(sources, entryClass);
        return CompileAndRunC(c, stress, extraFlags);
    }

    public static string CompileAndRunC(string cSource, bool stress = false,
        IEnumerable<string>? extraFlags = null, string? extraCSource = null)
    {
        var compiler = CCompiler
            ?? throw new InvalidOperationException("no C compiler available");
        var dir = Directory.CreateTempSubdirectory("tcs2c-test-");
        try
        {
            var cPath = Path.Combine(dir.FullName, "program.c");
            File.WriteAllText(cPath, cSource);
            var exe = Path.Combine(dir.FullName,
                OperatingSystem.IsWindows() ? "program.exe" : "program");
            var args = new List<string>(StrictFlags);
            if (stress) args.Add("-DTCS_GC_STRESS=1");
            if (extraFlags != null) args.AddRange(extraFlags);
            args.Add(cPath);
            if (extraCSource != null)
            {
                var hostPath = Path.Combine(dir.FullName, "host.c");
                File.WriteAllText(hostPath, extraCSource);
                args.Add(hostPath);
            }
            args.AddRange(["-o", exe, "-lm"]);
            var compile = Run(Start(compiler, args), CompileTimeout);
            if (compile.ExitCode != 0)
                throw new InvalidOperationException(
                    $"C compile failed:\n{compile.Stderr}\n--- C ---\n{Number(cSource)}");
            var run = Run(Start(exe, []), RunTimeout);
            if (run.ExitCode != 0)
                throw new InvalidOperationException(
                    $"C program exited with {run.ExitCode}:\n{run.Stderr}\n" +
                    $"stdout:\n{run.Stdout}");
            return run.Stdout.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>tcs → Lua → lua32 (LUA_32BITS)。entryClass.Main() を呼ぶ。</summary>
    public static string RunLua(string[] sources, string entryClass) =>
        RunLuaScript(sources, $"{entryClass}.{LuaNaming.Member("Main")}()\n");

    private static string LibCalls(string cls, int frames) =>
        $"{cls}.{LuaNaming.Member("Setup")}()\n" +
        $"for _ = 1, {frames} do {cls}.{LuaNaming.Member("Frame")}() end\n" +
        $"{cls}.{LuaNaming.Member("Report")}()\n";

    /// <summary>lib 形の host と同じ順で Setup / Frame×N / Report を呼ぶ。</summary>
    public static string RunLuaLib(string[] sources, string cls, int frames) =>
        RunLuaScript(sources, LibCalls(cls, frames));

    /// <summary>--lib の C を host (別 translation unit) から呼ぶ:
    /// tcs_lib_init → Setup → (Frame → tcs_lib_gc)×N → Report。</summary>
    public static string RunCLib(string[] sources, string cls, int frames,
        bool stress = false)
    {
        var c = EmitC(sources, null, lib: true);
        var host = $$"""
            void tcs_lib_init(void);
            void tcs_lib_gc(void);
            void tcs_entry_{{cls}}_setup(void);
            void tcs_entry_{{cls}}_frame(void);
            void tcs_entry_{{cls}}_report(void);
            int main(void)
            {
                int i;
                tcs_lib_init();
                tcs_entry_{{cls}}_setup();
                tcs_lib_gc();
                for (i = 0; i < {{frames}}; i++) {
                    tcs_entry_{{cls}}_frame();
                    tcs_lib_gc();
                }
                tcs_entry_{{cls}}_report();
                return 0;
            }
            """;
        return CompileAndRunC(c, stress, extraCSource: host);
    }

    private static string RunLuaScript(string[] sources, string calls)
    {
        var lua = Transpiler.Transpile(sources);
        // TranspileAndRunWithRuntime と同じ runtime 束縛 (List / Dict / ... は
        // runtime/tinysystem.lua の table)
        var runtimePath = Path.Combine(ProjectRoot, "runtime", "tinysystem.lua")
            .Replace("\\", "/");
        var script = $"local TinySystem = dofile(\"{runtimePath}\")\n" +
            "List = TinySystem.List\nDict = TinySystem.Dict\nMath = TinySystem.Math\n" +
            "String = TinySystem.String\nRandom = TinySystem.Random\nChar = TinySystem.Char\n" +
            $"{lua}\n{calls}";
        var dir = Directory.CreateTempSubdirectory("tcs2c-lua-");
        try
        {
            var path = Path.Combine(dir.FullName, "program.lua");
            File.WriteAllText(path, script);
            var run = Run(Start(LuaPath, [path]), RunTimeout);
            if (run.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Lua exited with {run.ExitCode}:\n{run.Stderr}\n--- lua ---\n{script}");
            return run.Stdout.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>C (通常 + GC stress) と Lua の stdout 一致を要求する。</summary>
    public static string AssertParity(string[] sources, string entryClass)
    {
        var lua = RunLua(sources, entryClass);
        var c = RunC(sources, entryClass);
        Assert.Equal(lua, c);
        var stressed = RunC(sources, entryClass, stress: true);
        Assert.Equal(lua, stressed);
        return lua;
    }

    public static string AssertParity(string source, string entryClass) =>
        AssertParity([source], entryClass);

    /// <summary>lib 形 (フレーム境界 GC あり) で C (通常 + stress) と Lua の
    /// stdout 一致を要求する。</summary>
    public static string AssertParityLib(string source, string cls, int frames)
    {
        var lua = RunLuaLib([source], cls, frames);
        Assert.Equal(lua, RunCLib([source], cls, frames));
        Assert.Equal(lua, RunCLib([source], cls, frames, stress: true));
        return lua;
    }

    private static string Number(string text) => string.Join("\n",
        text.Split('\n').Select((l, i) => $"{i + 1,5}: {l}"));

    private static ProcessStartInfo Start(string file, IEnumerable<string> args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    private static (int ExitCode, string Stdout, string Stderr) Run(
        ProcessStartInfo psi, TimeSpan timeout)
    {
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"cannot start {psi.FileName}");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new TimeoutException($"{psi.FileName} timed out after {timeout}");
        }
        return (process.ExitCode, stdout.GetAwaiter().GetResult(),
            stderr.GetAwaiter().GetResult());
    }
}

/// <summary>C compiler が無い環境では skip する Fact。</summary>
public sealed class CFactAttribute : FactAttribute
{
    public CFactAttribute()
    {
        if (Backends.CCompiler is null) Skip = "no C compiler (gcc/cc/clang) on PATH";
    }
}

public sealed class CTheoryAttribute : TheoryAttribute
{
    public CTheoryAttribute()
    {
        if (Backends.CCompiler is null) Skip = "no C compiler (gcc/cc/clang) on PATH";
    }
}
