using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TinyCs;
using TinyCs.Tcs2c;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// 実行系の差分ハーネス。同じ TinyC# source を tcs2c→C→gcc、tcs VM (VmBackend.cs)、実 .NET で実行し、stdout を突き合わせる。
/// C の stdout を基準に、C の GC stress と VM (通常 + GC stress) が同じ出力で、実 .NET (TinySystem.props と同じ構成) とも
/// 一致することを要求する。.NET が意図して違う結果を出す program (System.Random の列、範囲外の (char) が止まらない、
/// UTF-16 に入らない byte 列) は AssertBackendParity / AssertBackendFaultParity で .NET を外し、期待の stdout を呼び出し側が持つ。
/// GC はフレーム境界 (lib 出荷形の tcs_lib_gc) でだけ走るので、GC の意味論は lib 形で
/// Setup / Frame×N / Report を回す AssertParityLib で検証する。C 側の
/// GC stress (-DTCS_GC_STRESS=1: 毎境界で旧世代 full GC + 256 byte の
/// nursery chunk) では、root / ライトバリアの漏れは、生きている object が回収 / 上書きされて出力が変わる形で現れる。
/// </summary>
internal static partial class Backends
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);
    public static readonly string ProjectRoot = FindProjectRoot();
    public static readonly string? CCompiler = FindCCompiler();

    public static readonly string[] StrictFlags =
        ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard",
            // 型記述子 (doc/object-model.md) の offset と生成 struct の offsetof の一致を初期化で確かめる
            "-DTCS_VERIFY_TYPES"];

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
        bool lib = false, bool digestF32 = false) =>
        SelfHostC.Emit(sources, entryClass, lib, digestF32);

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
                throw Exited($"C program exited with {run.ExitCode}:\n{run.Stderr}\n" +
                    $"stdout:\n{run.Stdout}", run.Stdout);
            return run.Stdout.Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            try { dir.Delete(recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>--lib の C を host (別 translation unit) から呼ぶ:
    /// tcs_lib_init → Setup → (Frame → tcs_lib_gc)×N → Report。</summary>
    public static string RunCLib(string[] sources, string cls, int frames,
        bool stress = false)
    {
        var c = EmitC(sources, null, lib: true);
        var host = $$"""
            void tcs_lib_init(void);
            void tcs_lib_gc(void);
            void tcs_entry_{{cls}}_Setup(void);
            void tcs_entry_{{cls}}_Frame(void);
            void tcs_entry_{{cls}}_Report(void);
            int main(void)
            {
                int i;
                tcs_lib_init();
                tcs_entry_{{cls}}_Setup();
                tcs_lib_gc();
                for (i = 0; i < {{frames}}; i++) {
                    tcs_entry_{{cls}}_Frame();
                    tcs_lib_gc();
                }
                tcs_entry_{{cls}}_Report();
                return 0;
            }
            """;
        return CompileAndRunC(c, stress, extraCSource: host);
    }

    /// <summary>C (通常 + GC stress)、VM、実 .NET の stdout 一致を要求し、その stdout を返す。</summary>
    public static string AssertParity(string[] sources, string entryClass)
    {
        var c = AssertBackendParity(sources, entryClass);
        Assert.Equal(RunDotnet(sources, entryClass), c);
        return c;
    }

    public static string AssertParity(string source, string entryClass) =>
        AssertParity([source], entryClass);

    /// <summary>C (通常 + GC stress) と VM の stdout 一致を要求し、C の stdout を返す。.NET を外すのは、
    /// .NET が意図して違う結果を出す program だけ (期待の stdout は呼び出し側が持つ)。</summary>
    public static string AssertBackendParity(string[] sources, string entryClass)
    {
        var c = RunC(sources, entryClass);
        Assert.Equal(c, RunC(sources, entryClass, stress: true));
        if (VmEnabled) Assert.Equal(c, RunVm(sources, entryClass));
        return c;
    }

    public static string AssertBackendParity(string source, string entryClass) =>
        AssertBackendParity([source], entryClass);

    /// <summary>C (通常 + GC stress) と VM が fault で止まり、実 .NET が例外で止まり、そこまでの
    /// stdout が一致することを要求する (il-spec §12)。その stdout を返す。</summary>
    public static string AssertFaultParity(string source, string entryClass)
    {
        var c = AssertBackendFaultParity(source, entryClass);
        Assert.Equal(FaultedStdoutDotnet(source, entryClass), c);
        return c;
    }

    /// <summary>C (通常 + GC stress) と VM がともに fault で止まり、fault までの stdout が一致することを要求する。
    /// .NET を外すのは、.NET が止まらない fault (tcs が C# より狭く定める値) だけ。</summary>
    public static string AssertBackendFaultParity(string source, string entryClass)
    {
        var c = FaultedStdout(() => RunC([source], entryClass));
        Assert.Equal(c, FaultedStdout(() => RunC([source], entryClass, stress: true)));
        if (VmEnabled) Assert.Equal(c, FaultedStdout(() => RunVm([source], entryClass)));
        return c;
    }

    /// <summary>同じ source を実 .NET で実行した stdout (Console.Out は process で 1 つなので順に動かす)。
    /// TinySystem.props と同じ構成で compile する: Math / MathF は TinySystem.Math (超越関数が runtime/math と bit 一致)、
    /// TinySystem.Linq は source、Dictionary / HashSet は TinySystem の実装 (挿入順)。string の既定の比較は
    /// test の process の InvariantGlobalization (tcs2c.Tests.csproj) で ordinal。frames を渡すと lib 形
    /// (Setup → Frame×frames → Report) で呼ぶ。</summary>
    public static string RunDotnet(string[] sources, string entryClass, int? frames = null)
    {
        var compilation = CSharpCompilation.Create("TcsDotnetRun",
            [.. sources.Select(s => CSharpSyntaxTree.ParseText(s)), .. DotnetLibrary.Value],
            DotnetReferences.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
            throw new InvalidOperationException("C# compile failed:\n" +
                string.Join("\n", emitted.Diagnostics));
        image.Position = 0;
        var context = new AssemblyLoadContext(null, isCollectible: true);
        var type = context.LoadFromStream(image).GetType(entryClass)!;
        lock (DotnetConsole)
        {
            var saved = Console.Out;
            var writer = new StringWriter { NewLine = "\n" };
            Console.SetOut(writer);
            try
            {
                if (frames is { } n)
                {
                    type.GetMethod("Setup")!.Invoke(null, null);
                    for (var i = 0; i < n; i++) type.GetMethod("Frame")!.Invoke(null, null);
                    type.GetMethod("Report")!.Invoke(null, null);
                }
                else
                {
                    type.GetMethod("Main")!.Invoke(null, null);
                }
            }
            catch (TargetInvocationException thrown)
            {
                throw Exited($".NET threw: {thrown.InnerException}", writer.ToString());
            }
            finally
            {
                Console.SetOut(saved);
                context.Unload();
            }
            return writer.ToString().TrimEnd('\n');
        }
    }

    public static string RunDotnet(string source, string entryClass) => RunDotnet([source], entryClass);

    // TinySystem.props が .NET の project に入れるもの
    private static readonly Lazy<SyntaxTree[]> DotnetLibrary = new(() =>
    [
        CSharpSyntaxTree.ParseText("global using Math = TinySystem.Math;\nglobal using MathF = TinySystem.Math;\n"),
        CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(ProjectRoot, "TinySystem", "Collections.cs"))),
        CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(ProjectRoot, "TinySystem", "Linq.cs"))),
    ]);

    private static readonly Lazy<MetadataReference[]> DotnetReferences = new(() =>
        [.. ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(path => MetadataReference.CreateFromFile(path))]);

    private static readonly object DotnetConsole = new();

    private static string FaultedStdoutDotnet(string source, string entryClass) =>
        FaultedStdout(() => RunDotnet(source, entryClass));

    private static string FaultedStdout(Func<string> run)
    {
        try
        {
            var stdout = run();
            throw new Xunit.Sdk.XunitException($"expected a fault, but completed:\n{stdout}");
        }
        catch (InvalidOperationException exited)
            when (exited.Data[ExitedStdout] is string stdout)
        {
            return stdout.Replace("\r\n", "\n").TrimEnd('\n');
        }
    }

    // 非 0 終了 (fault を含む) の例外。実行できた program の stdout を Data に持つ
    private const string ExitedStdout = "stdout";

    private static InvalidOperationException Exited(string message, string stdout)
    {
        var error = new InvalidOperationException(message);
        error.Data[ExitedStdout] = stdout;
        return error;
    }

    /// <summary>lib 形 (フレーム境界 GC あり) で C (通常 + stress)、VM、実 .NET (同じ順に
    /// Setup / Frame×N / Report を呼ぶ) の stdout 一致を要求し、その stdout を返す。</summary>
    public static string AssertParityLib(string source, string cls, int frames)
    {
        var c = RunCLib([source], cls, frames);
        Assert.Equal(c, RunCLib([source], cls, frames, stress: true));
        if (VmEnabled) Assert.Equal(c, RunVmLib([source], cls, frames));
        Assert.Equal(RunDotnet([source], cls, frames), c);
        return c;
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
