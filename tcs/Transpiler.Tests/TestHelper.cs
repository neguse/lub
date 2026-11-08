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
    /// C# の program に `Console.WriteLine(csharpExpr)` の entry を足して tcs VM で走らせ、stdout を返す (VmCorpus.Run)。
    /// TCS_DIFFERENTIAL=1 なら実 .NET の stdout とも一致を要求する。dotnet: false は .NET と意図して違う program (理由は呼び出し側に書く)。
    /// </summary>
    public static string CompileAndRunVm(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSource, csharpExpr, dotnet);

    /// <summary>複数の file の program の CompileAndRunVm。</summary>
    public static string CompileAndRunVm(string[] csharpSources, string csharpExpr, bool dotnet = true) =>
        VmCorpus.Run(csharpSources, csharpExpr, dotnet);

    /// <summary>本文が mainBody の entry (`TcsEntry.Main`) を足して tcs VM で走らせる。本文が `Console.WriteLine` で結果を書く。</summary>
    public static string CompileAndRunVmMain(string csharpSource, string mainBody, bool dotnet = true) =>
        VmCorpus.RunMain(csharpSource, mainBody, dotnet);

    /// <summary>program 全体を tcs VM で走らせた stdout。entryClass は static Main を持つ class (null なら top-level 文)。
    /// TCS_DIFFERENTIAL=1 なら実 .NET の stdout とも一致を要求する。</summary>
    public static string CompileAndRunVmProgram(string[] sources, string? entryClass = null, bool dotnet = true) =>
        VmCorpus.RunWhole(sources, entryClass, dotnet);

    /// <summary>top-level 文の program: 最後の top-level 文の後で `Console.WriteLine(csharpExpr)` を走らせる (式は top-level の local を読める)。</summary>
    public static string CompileAndRunVmTopLevel(string csharpSource, string csharpExpr, bool dotnet = true) =>
        VmCorpus.RunTopLevel(csharpSource, csharpExpr, dotnet);
}
