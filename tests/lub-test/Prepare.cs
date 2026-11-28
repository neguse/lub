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

    /// <summary>The command that starts the .NET tcs (src/tcs built into build-tcs/): the program and its first arguments.
    /// The output directory is given, so the place of tcs.dll does not depend on the target framework or on Platform in the environment.</summary>
    public static async Task<string[]> Tcs(Run run)
    {
        var site = run.Site;
        await site.Need("tcs", () => run.Must("dotnet", ["build", site.Path_("src", "tcs"), "-c", "Release", "-nologo", "-v", "q", "-o", site.Path_("build-tcs")],
            timeout: TimeSpan.FromMinutes(20)));
        return ["dotnet", site.Path_("build-tcs", "tcs.dll").Replace('\\', '/')];
    }

    /// <summary>The environment for programs that start tcs themselves (lub, the scripts): LUB_TCS is the one name for "the tcs to use".</summary>
    public static async Task<Dictionary<string, string?>> TcsEnv(Run run) => new() { ["LUB_TCS"] = string.Join(" ", await Tcs(run)) };

    /// <summary>The inputs of a C# program given to lub as a directory: every .cs under it (not in bin, obj or hidden directories), then the implementation sources of cs-lib
    /// (the same convention as the lub CLI). Paths are relative to the repo root, in a fixed order.</summary>
    public static List<string> Sources(Site site, string directory)
    {
        static bool Skipped(string root, string file) =>
            Path.GetRelativePath(root, file).Split(Path.DirectorySeparatorChar)[..^1].Any(p => p is "bin" or "obj" || p.StartsWith('.'));
        var lib = site.Path_("cs-lib");
        var sources = Directory.GetFiles(directory, "*.cs", SearchOption.AllDirectories).Where(f => !Skipped(directory, f)).Order(StringComparer.Ordinal).ToList();
        // lub_stub.cs only declares the API, and is given as --ref
        sources.AddRange(Directory.GetFiles(lib, "*.cs", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "lub_stub.cs").Order(StringComparer.Ordinal));
        return [.. sources.Select(f => Path.GetRelativePath(site.Root, f).Replace('\\', '/'))];
    }

    /// <summary>lub-gen on .NET (tools/lub-gen built once into build-lub-gen/).</summary>
    public static async Task<Output> LubGen(Run run, IEnumerable<string> args)
    {
        var site = run.Site;
        await site.Need("lub-gen", () => run.Must("dotnet", ["build", site.Path_("tools", "lub-gen"), "-c", "Release", "-nologo", "-v", "q", "-o", site.Path_("build-lub-gen")],
            timeout: TimeSpan.FromMinutes(20)));
        return await run.Exec("dotnet", [site.Path_("build-lub-gen", "lub-gen.dll"), .. args], timeout: TimeSpan.FromMinutes(10));
    }

    public static async Task<Output> MustLubGen(Run run, IEnumerable<string> args)
    {
        var output = await LubGen(run, args);
        if (!output.Ok) run.Fail("lub-gen exited with " + output.ExitCode);
        return output;
    }

    /// <summary>The tcs VM as a library (src/tcs-vm built once into build-tcs-vm/): the compiler arguments that include and link it.</summary>
    public static async Task<string[]> TcsVm(Run run)
    {
        var site = run.Site;
        var build = site.Path_("build-tcs-vm");
        await site.Need("tcs-vm", async () =>
        {
            await run.Must("cmake", ["-S", site.Path_("src", "tcs-vm"), "-B", build, "-DCMAKE_BUILD_TYPE=Release"], timeout: TimeSpan.FromMinutes(10));
            await run.Must("cmake", ["--build", build, "--target", "tcsvm", "--parallel"], timeout: TimeSpan.FromMinutes(20));
        });
        return ["-I" + site.Path_("src", "tcs-vm"), Path.Combine(build, "libtcs-vm.a"), Path.Combine(build, "tcs-runtime", "libtcs-runtime.a")];
    }

    /// <summary>The entry class of a sample directory: the name of its .csproj, or of its only .cs.</summary>
    public static string SampleEntry(string directory)
    {
        var projects = Directory.GetFiles(directory, "*.csproj").Order(StringComparer.Ordinal).ToArray();
        if (projects.Length > 0) return Path.GetFileNameWithoutExtension(projects[0]);
        var sources = Directory.GetFiles(directory, "*.cs");
        if (sources.Length == 1) return Path.GetFileNameWithoutExtension(sources[0]);
        throw new TestFailed("several .cs but no .csproj in " + directory + " (the entry class is ambiguous)");
    }

    /// <summary>Compiles a sample (samples/&lt;name&gt;) into a tcs VM module, samples/&lt;name&gt;/.lub/&lt;Entry&gt;.tcsb, and returns its path.</summary>
    public static async Task<string> SampleModule(Run run, string name)
    {
        var site = run.Site;
        var dir = site.Path_("samples", name);
        var entry = SampleEntry(dir);
        var module = Path.Combine(dir, ".lub", entry + ".tcsb");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        await run.MustTcs(["vm", .. Sources(site, dir), "--ref", "cs-lib/lub_stub.cs", "--export", entry + ".*", "-o", module]);
        return module;
    }

    /// <summary>Configures and builds a lub build directory in Release (what scripts/build-release.sh does), with extra -D options.</summary>
    public static async Task BuildLub(Run run, string build, params string[] defines)
    {
        string[] generator = Site.Which("ninja") != null ? ["-G", "Ninja"] : [];
        await run.Must("cmake", ["-S", run.Site.Root, "-B", build, "-DCMAKE_BUILD_TYPE=Release", .. defines, .. generator], timeout: TimeSpan.FromHours(1));
        await run.Must("cmake", ["--build", build, "--target", "lub", "--parallel"], timeout: TimeSpan.FromHours(1));
    }

    /// <summary>All of tests/cs as one tcs VM module. A test is selected with --entry.</summary>
    public static string AllTcsb(Site site) => site.Path_("tests", "cs", ".lub", "all.tcsb");

    public static Task CsTests(Run run) => run.Site.Need("all.tcsb", async () =>
    {
        var site = run.Site;
        Directory.CreateDirectory(Path.GetDirectoryName(AllTcsb(site))!);
        var exports = Directory.GetFiles(site.Path_("tests", "cs"), "*.csproj").Order(StringComparer.Ordinal)
            .SelectMany(p => new[] { "--export", Path.GetFileNameWithoutExtension(p) + ".*" });
        await run.MustTcs(["vm", .. Sources(site, site.Path_("tests", "cs")), "--ref", "cs-lib/lub_stub.cs", .. exports, "-o", "tests/cs/.lub/all.tcsb"]);
    });

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
