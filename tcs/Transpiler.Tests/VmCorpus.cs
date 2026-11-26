using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TinyCs.Vm;

namespace TinyCs.Tests;

/// <summary>
/// Runs semantic tests (CompileAndRunVm in docs/tcs/vm-design.md §16): adds an entry `Console.WriteLine(expr)` to a C# program,
/// runs it on the tcs VM (tcs2vm → tcsvm, normal and GC stress), and returns stdout. If TCS_DIFFERENTIAL=1, the same entry is also run on real .NET
/// (with the same setup as TinySystem.props), and the stdout must match (dotnet differential).
/// </summary>
public static class VmCorpus
{
    private static readonly object Lock = new();
    private static string? _exe;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private static bool DifferentialEnabled => Environment.GetEnvironmentVariable("TCS_DIFFERENTIAL") == "1";

    /// <summary>Adds `TcsEntry.Main` after the program's types and runs `Console.WriteLine(csharpExpr)`.
    /// dotnet: false skips the comparison with .NET (a program whose meaning intentionally differs from .NET; the caller writes the reason).</summary>
    public static string Run(string csharpSource, string csharpExpr, bool dotnet = true) =>
        Run([csharpSource], csharpExpr, dotnet);

    /// <summary>Adds a `TcsEntry.Main` file to a program of multiple files.</summary>
    public static string Run(string[] csharpSources, string csharpExpr, bool dotnet = true)
    {
        string[] sources = [.. csharpSources, "public static class TcsEntry { public static void Main() { System.Console.WriteLine("
            + csharpExpr + "); } }\n"];
        var vm = RunPrograms(sources, "TcsEntry");
        Differential(sources, "TcsEntry", vm, dotnet, csharpExpr);
        return vm;
    }

    /// <summary>Adds `TcsEntry.Main` with mainBody as its body after the program's types and runs it (the body writes to stdout).</summary>
    public static string RunMain(string csharpSource, string mainBody, bool dotnet = true)
    {
        var probe = csharpSource + "\npublic static class TcsEntry { public static void Main() { " + mainBody + " } }\n";
        var vm = RunProgram(probe, "TcsEntry");
        Differential(probe, "TcsEntry", vm, dotnet, mainBody);
        return vm;
    }

    /// <summary>Runs the whole program. entryClass is the class with static Main (null for top-level statements).</summary>
    public static string RunWhole(string[] sources, string? entryClass, bool dotnet = true)
    {
        var vm = RunPrograms(sources, entryClass);
        Differential(sources, entryClass, vm, dotnet, entryClass ?? "<top-level>");
        return vm;
    }

    /// <summary>Top-level statement program: adds `Console.WriteLine(csharpExpr)` after the last top-level statement and runs it
    /// (the expression can read top-level locals).</summary>
    public static string RunTopLevel(string csharpSource, string csharpExpr, bool dotnet = true)
    {
        var root = CSharpSyntaxTree.ParseText(csharpSource).GetRoot();
        var last = root.DescendantNodes().OfType<GlobalStatementSyntax>().LastOrDefault()
            ?? throw new ArgumentException("the program has no top-level statement");
        var at = last.Span.End;
        var probe = csharpSource[..at] + "\nSystem.Console.WriteLine(" + csharpExpr + ");\n" + csharpSource[at..];
        var vm = RunProgram(probe, null);
        Differential(probe, null, vm, dotnet, csharpExpr);
        return vm;
    }

    /// <summary>stdout of running the program on the tcs VM (normal and GC stress must match). Top-level statements if entryClass is null.
    /// TinyC# diagnostics are InvalidOperationException, VM errors are VmException, and faults are InvalidOperationException carrying stderr.</summary>
    public static string RunProgram(string program, string? entryClass) => RunPrograms([program], entryClass);

    /// <param name="refs">Sources passed for types and declarations only, like --ref (the implicit global usings of spec examples)</param>
    public static string RunPrograms(string[] programs, string? entryClass, string[]? refs = null)
    {
        var bytes = SelfHostVm.Emit(programs, entryClass, refs: refs);
        var dir = Directory.CreateTempSubdirectory("tcsvm-corpus-");
        try
        {
            var path = Path.Combine(dir.FullName, "p.tcsb");
            File.WriteAllBytes(path, bytes);
            string? first = null;
            foreach (var stress in new[] { false, true })
            {
                var (code, stdout, stderr) = Run(Exe(), stress ? ["--gc-stress", path] : [path]);
                if (code != 0)
                    throw new InvalidOperationException($"tcsvm{(stress ? " (gc stress)" : "")} exited with {code}:\n{stderr}\nstdout:\n{stdout}");
                var output = stdout.Replace("\r\n", "\n").TrimEnd('\n');
                if (first != null && first != output)
                    throw new InvalidOperationException($"tcsvm differs under --gc-stress:\n{first}\n---\n{output}");
                first = output;
            }
            return first!;
        }
        finally
        {
            try { dir.Delete(true); } catch (IOException) { }
        }
    }

    private static void Differential(string program, string? entryClass, string vm, bool dotnet, string expr) =>
        Differential([program], entryClass, vm, dotnet, expr);

    private static void Differential(string[] programs, string? entryClass, string vm, bool dotnet, string expr)
    {
        if (!DifferentialEnabled) return;
        if (!dotnet) return;
        // The System.Random sequence differs from the agreed PRNG (il-spec §13)
        if (programs.Any(p => p.Contains("Random", StringComparison.Ordinal))) return;
        var net = RunDotnet(programs, entryClass);
        if (net != vm)
            throw new InvalidOperationException($"dotnet differential mismatch\nexpr:   {expr}\ndotnet: {net}\nvm:     {vm}");
    }

    // What TinySystem.props adds to a .NET project (Linq.cs is in TcsLibrary.LibraryTrees)
    private static readonly Lazy<SyntaxTree[]> DotnetLibrary = new(() =>
    [
        CSharpSyntaxTree.ParseText("global using Math = TinySystem.Math;\nglobal using MathF = TinySystem.Math;\n"),
        CSharpSyntaxTree.ParseText(File.ReadAllText(TestHelper.FindProjectFile("TinySystem/Collections.cs"))),
        .. TcsLibrary.LibraryTrees.Where(t => t.FilePath != TcsLibrary.ValueTupleLibraryPath),
    ]);

    /// <summary>stdout of running the entry on real .NET. A top-level statement program if entryClass is null.</summary>
    public static string RunDotnet(string[] programs, string? entryClass)
    {
        var compilation = CSharpCompilation.Create("TcsVmCorpus",
            [.. programs.Select(p => CSharpSyntaxTree.ParseText(p)), .. DotnetLibrary.Value],
            TcsLibrary.References,
            new CSharpCompilationOptions(entryClass == null ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
                concurrentBuild: false));
        using var image = new MemoryStream();
        var emitted = compilation.Emit(image);
        if (!emitted.Success)
            throw new InvalidOperationException("C# compile failed:\n" +
                string.Join("\n", emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
        image.Position = 0;
        var context = new AssemblyLoadContext("TcsVmCorpus", isCollectible: true);
        try
        {
            var assembly = context.LoadFromStream(image);
            var main = entryClass == null
                ? assembly.EntryPoint!
                : assembly.GetType(entryClass)!.GetMethod("Main", BindingFlags.Public | BindingFlags.Static)!;
            using var writer = new StringWriter { NewLine = "\n" };
            using (ConsoleCapture.BeginOut(writer))
            {
                // ConsoleCapture's AsyncLocal flows to the Task.Run child (same as SpecDotnetExecutor)
                var invocation = Task.Run(() => main.Invoke(null, main.GetParameters().Length == 0 ? null : [Array.Empty<string>()]));
                try
                {
                    if (!invocation.Wait(RunTimeout))
                        throw new InvalidOperationException($".NET timed out after {RunTimeout}");
                }
                catch (AggregateException e)
                {
                    var inner = e.InnerException is TargetInvocationException t ? t.InnerException ?? t : e.InnerException ?? e;
                    throw new InvalidOperationException($".NET threw: {inner.GetType().Name}: {inner.Message}\nstdout:\n{writer}", inner);
                }
            }
            return writer.ToString().Replace("\r\n", "\n").TrimEnd('\n');
        }
        finally
        {
            context.Unload();
        }
    }

    private static (int, string, string) Run(string exe, string[] args)
    {
        using var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        })!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(RunTimeout)) p.Kill(true);
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    /// <summary>Builds tcsvm from source only once.</summary>
    private static string Exe()
    {
        lock (Lock)
        {
            if (_exe is not null) return _exe;
            var root = TestHelper.ProjectRootPath;
            var dir = Directory.CreateTempSubdirectory("tcsvm-bin-");
            var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "tcsvm.exe" : "tcsvm");
            var args = new List<string> { "-std=c11", "-O1", "-ffp-contract=off", "-fno-fast-math" };
            args.AddRange(Directory.GetFiles(Path.Combine(root, "..", "src", "tcs-vm"), "*.c"));
            foreach (var dep in new[] { "sort", "fmt", "math", "utf8", "model" })
                args.AddRange(Directory.GetFiles(Path.Combine(root, "..", "src", "tcs-runtime", dep), "*.c"));
            args.AddRange(["-o", exe, "-lm"]);
            using var p = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("CC") ?? "cc", args)
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            var err = p.StandardError.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) throw new InvalidOperationException("tcsvm build failed:\n" + err);
            return _exe = exe;
        }
    }
}
