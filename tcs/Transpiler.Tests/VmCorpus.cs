using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using TinyCs.Vm;

namespace TinyCs.Tests;

/// <summary>
/// semantic テストの実行 (doc/vm-design.md §16 の CompileAndRunVm): C# の program に `Console.WriteLine(式)` の entry を足し、
/// tcs VM (tcs2vm → tcsvm、通常と GC stress) で走らせた stdout を返す。TCS_DIFFERENTIAL=1 なら同じ entry を実 .NET
/// (TinySystem.props と同じ構成) でも走らせ、stdout の一致を要求する (dotnet differential)。
/// TCS_DIFFERENTIAL_LOG に、.NET と比べたか、比べなかった理由を 1 行ずつ足す。
/// </summary>
public static class VmCorpus
{
    private static readonly object Lock = new();
    private static string? _exe;
    private static readonly TimeSpan RunTimeout = TimeSpan.FromSeconds(30);

    private static bool DifferentialEnabled => Environment.GetEnvironmentVariable("TCS_DIFFERENTIAL") == "1";

    /// <summary>program の型の後に `TcsEntry.Main` を足し、`Console.WriteLine(csharpExpr)` を走らせる。
    /// dotnet: false は .NET と比べない (.NET と意図して違う意味の program。理由は呼び出し側に書く)。</summary>
    public static string Run(string csharpSource, string csharpExpr, bool dotnet = true) =>
        Run([csharpSource], csharpExpr, dotnet);

    /// <summary>複数の file の program に `TcsEntry.Main` の file を足す。</summary>
    public static string Run(string[] csharpSources, string csharpExpr, bool dotnet = true)
    {
        string[] sources = [.. csharpSources, "public static class TcsEntry { public static void Main() { System.Console.WriteLine("
            + csharpExpr + "); } }\n"];
        var vm = RunPrograms(sources, "TcsEntry");
        Differential(sources, "TcsEntry", vm, dotnet, csharpExpr);
        return vm;
    }

    /// <summary>program の型の後に、本文が mainBody の `TcsEntry.Main` を足して走らせる (本文が stdout に書く)。</summary>
    public static string RunMain(string csharpSource, string mainBody, bool dotnet = true)
    {
        var probe = csharpSource + "\npublic static class TcsEntry { public static void Main() { " + mainBody + " } }\n";
        var vm = RunProgram(probe, "TcsEntry");
        Differential(probe, "TcsEntry", vm, dotnet, mainBody);
        return vm;
    }

    /// <summary>program 全体を走らせる。entryClass は static Main を持つ class (null なら top-level 文)。</summary>
    public static string RunWhole(string[] sources, string? entryClass, bool dotnet = true)
    {
        var vm = RunPrograms(sources, entryClass);
        Differential(sources, entryClass, vm, dotnet, entryClass ?? "<top-level>");
        return vm;
    }

    /// <summary>top-level 文の program: 最後の top-level 文の後に `Console.WriteLine(csharpExpr)` を足して走らせる
    /// (式は top-level の local を読める)。</summary>
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

    /// <summary>program を tcs VM で走らせた stdout (通常と GC stress が同じであること)。entryClass が null なら top-level 文。
    /// TinyC# の診断は InvalidOperationException、VM の誤りは VmException、fault は stderr を持つ InvalidOperationException。</summary>
    public static string RunProgram(string program, string? entryClass) => RunPrograms([program], entryClass);

    /// <param name="refs">--ref と同じく型と宣言だけを渡す source (spec の例の暗黙の global using)</param>
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
        if (!dotnet)
        {
            Log("skip-flagged", expr);
            return;
        }
        // System.Random の列は合意 PRNG と違う (il-spec §13)
        if (programs.Any(p => p.Contains("Random", StringComparison.Ordinal)))
        {
            Log("skip-random", expr);
            return;
        }
        var net = RunDotnet(programs, entryClass);
        if (net != vm)
        {
            Log("mismatch", expr);
            throw new InvalidOperationException($"dotnet differential mismatch\nexpr:   {expr}\ndotnet: {net}\nvm:     {vm}");
        }
        Log("match", expr);
    }

    // TinySystem.props が .NET の project に入れるもの (Linq.cs は TcsLibrary.LibraryTrees)
    private static readonly Lazy<SyntaxTree[]> DotnetLibrary = new(() =>
    [
        CSharpSyntaxTree.ParseText("global using Math = TinySystem.Math;\nglobal using MathF = TinySystem.Math;\n"),
        CSharpSyntaxTree.ParseText(File.ReadAllText(TestHelper.FindProjectFile("TinySystem/Collections.cs"))),
        .. TcsLibrary.LibraryTrees.Where(t => t.FilePath != TcsLibrary.ValueTupleLibraryPath),
    ]);

    /// <summary>実 .NET で entry を走らせた stdout。entryClass が null なら top-level 文の program。</summary>
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
                // ConsoleCapture の AsyncLocal は Task.Run の子へ流れる (SpecDotnetExecutor と同じ)
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

    /// <summary>tcsvm を source から 1 回だけ build する。</summary>
    private static string Exe()
    {
        lock (Lock)
        {
            if (_exe is not null) return _exe;
            var root = TestHelper.ProjectRootPath;
            var dir = Directory.CreateTempSubdirectory("tcsvm-bin-");
            var exe = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "tcsvm.exe" : "tcsvm");
            var args = new List<string> { "-std=c11", "-O1", "-ffp-contract=off", "-fno-fast-math" };
            args.AddRange(Directory.GetFiles(Path.Combine(root, "tcsvm", "vm"), "*.c"));
            foreach (var dep in new[] { "sort", "fmt", "math", "utf8", "model" })
                args.AddRange(Directory.GetFiles(Path.Combine(root, "runtime", dep), "*.c"));
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

    private static void Log(string kind, string expr)
    {
        if (Environment.GetEnvironmentVariable("TCS_DIFFERENTIAL_LOG") is not { Length: > 0 } path) return;
        lock (Lock) File.AppendAllText(path, $"{kind}\t{expr.ReplaceLineEndings(" ")}\n");
    }
}
