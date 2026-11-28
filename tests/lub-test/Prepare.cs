namespace LubTest;

/// <summary>Preparations shared by tests. Each runs once per invocation (Site.Need), and a test awaits the ones it needs.</summary>
public static class Prepare
{
    /// <summary>Builds a target of the lub build (the build directory is already configured: scripts/build-release.sh, or cmake -S . -B).</summary>
    public static Task Target(Run run, string target) => run.Site.Need("target:" + target, () => run.Site.WithCmake(async () =>
    {
        await run.Must("cmake", ["--build", run.Site.Build, "--target", target], timeout: TimeSpan.FromHours(1));
        return 0;
    }));

    public static Task Lub(Run run) => Target(run, "lub");

    /// <summary>The command that starts the .NET tcs (scripts/build-tcs-dotnet.sh builds it once and prints the command), as LUB_TCS.</summary>
    public static async Task<string> Tcs(Run run)
    {
        string? command = null;
        await run.Site.Need("tcs", async () =>
        {
            var built = await run.Script("scripts/build-tcs-dotnet.sh", [], timeout: TimeSpan.FromMinutes(20));
            TcsCommand = built.Text.Trim().Split('\n')[^1].Trim();
        });
        command = TcsCommand;
        return command ?? throw new TestFailed("the .NET tcs was not built");
    }

    private static string? TcsCommand;

    /// <summary>The environment for the scripts and programs that start tcs.</summary>
    public static async Task<Dictionary<string, string?>> TcsEnv(Run run) => new() { ["LUB_TCS"] = await Tcs(run) };

    /// <summary>All of tests/cs as one tcs VM module (scripts/tcs-cs-tests.sh). A test is selected with --entry.</summary>
    public static string AllTcsb(Site site) => site.Path_("tests", "cs", ".lub", "all.tcsb");

    public static Task CsTests(Run run) => run.Site.Need("all.tcsb", async () =>
        await run.Script("scripts/tcs-cs-tests.sh", ["vm"], await TcsEnv(run), TimeSpan.FromMinutes(20)));

    /// <summary>The .NET execution of all of tests/cs (dotnet/SampleRunner with -p:Tests=true). A test is selected with --entry.</summary>
    public static string DotnetTests(Site site) => site.Path_("dotnet", "SampleRunner", "bin", "tests", site.Windows ? "lub-sample-tests.exe" : "lub-sample-tests");

    public static Task DotnetCsTests(Run run) => run.Site.Need("dotnet-tests", async () =>
    {
        await Target(run, "lub_shared");
        await run.Must("dotnet", ["build", "dotnet/SampleRunner", "-p:Tests=true", "-nologo", "-v", "q"], timeout: TimeSpan.FromMinutes(20));
    });

    /// <summary>The shared library that .NET execution calls (the facade P/Invokes it), as LUB_NATIVE_LIB.</summary>
    public static Dictionary<string, string?> NativeLib(Site site) => new()
    {
        ["LUB_NATIVE_LIB"] = Path.Combine(site.Build, site.Windows ? "lub.dll" : site.Mac ? "liblub.dylib" : "liblub.so"),
    };
}
