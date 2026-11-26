using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using TinyCs;
using TinyCs.Tcs2c;

namespace TinyCs.Tcs2c.Tests;

/// <summary>
/// Differential harness for execution backends. Runs the same TinyC# source through tcs2c→C→gcc, the tcs VM (VmBackend.cs), and real .NET, and compares stdout.
/// With the C stdout as the baseline, it requires the C GC stress run and the VM (normal + GC stress) to produce the same output, and also to
/// match real .NET (same configuration as TinySystem.props). Programs where .NET intentionally gives different results (the System.Random sequence, out-of-range (char) not stopping,
/// byte sequences that do not fit in UTF-16) exclude .NET via AssertBackendParity / AssertBackendFaultParity, and the caller holds the expected stdout.
/// GC runs only at frame boundaries (tcs_lib_gc in the shipped lib form), so GC semantics are verified with AssertParityLib, which runs
/// Setup / Frame×N / Report in lib form. With the C-side
/// GC stress (-DTCS_GC_STRESS=1: an old-generation full GC at every boundary + 256-byte
/// nursery chunks), a missing root / write barrier shows up as a live object being collected / overwritten and the output changing.
/// </summary>
internal static partial class Backends
{
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(120);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(60);
    public static readonly string ProjectRoot = FindProjectRoot();
    public static readonly string? CCompiler = FindCCompiler();

    public static readonly string[] StrictFlags =
        ["-O2", "-ffp-contract=off", "-fwrapv", "-fexcess-precision=standard",
            // On initialization, checks that the offsets in the type descriptors (docs/tcs/object-model.md) match the offsetof of the generated structs
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

    /// <summary>Generates C source (emit only, no compile).</summary>
    public static string EmitC(string[] sources, string? entryClass,
        bool lib = false, bool digestF32 = false) =>
        SelfHostC.Emit(sources, entryClass, lib, digestF32);

    /// <summary>tcs2c → C compiler → run (main form: the whole Main is one frame and
    /// GC does not run).</summary>
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

    /// <summary>Calls the --lib C from a host (a separate translation unit):
    /// tcs_lib_init → Setup → (Frame → tcs_lib_gc)×N → Report.</summary>
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

    /// <summary>Requires stdout to match across C (normal + GC stress), the VM, and real .NET, and returns that stdout.</summary>
    public static string AssertParity(string[] sources, string entryClass)
    {
        var c = AssertBackendParity(sources, entryClass);
        Assert.Equal(RunDotnet(sources, entryClass), c);
        return c;
    }

    public static string AssertParity(string source, string entryClass) =>
        AssertParity([source], entryClass);

    /// <summary>Requires the stdout of C (normal + GC stress) and the VM to match, and returns the C stdout. .NET is excluded
    /// only for programs where .NET intentionally gives different results (the caller holds the expected stdout).</summary>
    public static string AssertBackendParity(string[] sources, string entryClass)
    {
        var c = RunC(sources, entryClass);
        Assert.Equal(c, RunC(sources, entryClass, stress: true));
        if (VmEnabled) Assert.Equal(c, RunVm(sources, entryClass));
        return c;
    }

    public static string AssertBackendParity(string source, string entryClass) =>
        AssertBackendParity([source], entryClass);

    /// <summary>Requires C (normal + GC stress) and the VM to stop with a fault, real .NET to stop with an exception, and the
    /// stdout up to that point to match (il-spec §12). Returns that stdout.</summary>
    public static string AssertFaultParity(string source, string entryClass)
    {
        var c = AssertBackendFaultParity(source, entryClass);
        Assert.Equal(FaultedStdoutDotnet(source, entryClass), c);
        return c;
    }

    /// <summary>Requires C (normal + GC stress) and the VM to both stop with a fault, and the stdout up to the fault to match.
    /// .NET is excluded only for faults where .NET does not stop (values that tcs defines more narrowly than C#).</summary>
    public static string AssertBackendFaultParity(string source, string entryClass)
    {
        var c = FaultedStdout(() => RunC([source], entryClass));
        Assert.Equal(c, FaultedStdout(() => RunC([source], entryClass, stress: true)));
        if (VmEnabled) Assert.Equal(c, FaultedStdout(() => RunVm([source], entryClass)));
        return c;
    }

    /// <summary>The stdout of running the same source on real .NET (Console.Out is one per process, so runs are sequential).
    /// Compiles with the same configuration as TinySystem.props: Math / MathF are TinySystem.Math (transcendental functions bit-identical to runtime/math),
    /// TinySystem.Linq is source, Dictionary / HashSet are TinySystem's implementations (insertion order). The default string comparison is
    /// ordinal because of the test process's InvariantGlobalization (tcs2c.Tests.csproj). If frames is passed, calls it in lib form
    /// (Setup → Frame×frames → Report).</summary>
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

    // What TinySystem.props adds to a .NET project
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

    // Exception for a nonzero exit (including faults). Holds the stdout of the program that ran in Data
    private const string ExitedStdout = "stdout";

    private static InvalidOperationException Exited(string message, string stdout)
    {
        var error = new InvalidOperationException(message);
        error.Data[ExitedStdout] = stdout;
        return error;
    }

    /// <summary>In lib form (with frame-boundary GC), requires stdout to match across C (normal + stress), the VM, and real .NET (which calls
    /// Setup / Frame×N / Report in the same order), and returns that stdout.</summary>
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

/// <summary>A Fact that is skipped in environments without a C compiler.</summary>
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
