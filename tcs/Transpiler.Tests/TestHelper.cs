namespace TinyCs.Tests;

public static class TestHelper
{
    private static readonly string ProjectRoot = FindProjectRoot();
    internal static string ProjectRootPath => ProjectRoot;

    private static string FindProjectRoot()
    {
        // Try multiple starting points
        string?[] starts = [
            AppContext.BaseDirectory,
            Path.GetDirectoryName(typeof(TestHelper).Assembly.Location),
            Environment.CurrentDirectory
        ];

        foreach (var start in starts)
        {
            if (string.IsNullOrEmpty(start)) continue;
            string? dir = start;
            while (!string.IsNullOrEmpty(dir))
            {
                if (File.Exists(Path.Combine(dir, "tcs.slnx"))) return dir;
                var parent = Path.GetDirectoryName(dir);
                if (parent == dir) break;
                dir = parent;
            }
        }
        return Environment.CurrentDirectory;
    }

    /// <summary>
    /// Resolve a path relative to the project root.
    /// </summary>
    public static string FindProjectFile(string relativePath)
    {
        var path = Path.Combine(ProjectRoot, relativePath);
        if (!File.Exists(path))
            throw new FileNotFoundException($"Project file not found: {path}");
        return path.Replace("\\", "/");
    }

    /// <summary>
    /// Adds an entry `Console.WriteLine(csharpExpr)` to a C# program, runs it on the tcs VM, and returns stdout (VmCorpus.Run).
    /// If TCS_DIFFERENTIAL=1, also requires a match with the stdout of real .NET. dotnet: false is for programs that intentionally differ from .NET (the caller writes the reason).
    /// </summary>
    public static string CompileAndRunVm(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSource, csharpExpr, dotnet);

    /// <summary>CompileAndRunVm for a program of multiple files.</summary>
    public static string CompileAndRunVm(string[] csharpSources, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSources, csharpExpr, dotnet);

    /// <summary>Adds an entry (`TcsEntry.Main`) whose body is mainBody and runs it on the tcs VM. The body writes the result with `Console.WriteLine`.</summary>
    public static string CompileAndRunVmMain(string csharpSource, string mainBody, bool dotnet = true) =>
        VmCorpus.RunMain(csharpSource, mainBody, dotnet);

    /// <summary>stdout of running the whole program on the tcs VM. entryClass is the class with static Main (null for top-level statements).
    /// If TCS_DIFFERENTIAL=1, also requires a match with the stdout of real .NET.</summary>
    public static string CompileAndRunVmProgram(string[] sources, string? entryClass = null, bool dotnet = true) =>
        VmCorpus.RunWhole(sources, entryClass, dotnet);

    /// <summary>Top-level statement program: runs `Console.WriteLine(csharpExpr)` after the last top-level statement (the expression can read top-level locals).</summary>
    public static string CompileAndRunVmTopLevel(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.RunTopLevel(csharpSource, csharpExpr, dotnet);
}
