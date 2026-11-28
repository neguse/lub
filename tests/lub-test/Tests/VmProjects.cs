namespace LubTest.Tests;

/// <summary>The path where the tcs VM player runs from a .csproj (tests/vm/&lt;name&gt;/): tcs vm --watch, hot reload, OnBuildFailed, the inspection entries of apps opened with App.Use, native extensions.
/// A test rewrites its files, so it is copied to a temporary directory and run there.
/// If the test has an `env` file, its lines (NAME=VALUE) are added to the environment.
/// With expect.txt the run must exit with 0 and each of its lines must appear in the output; with expect-crash.txt the run must fail and each of its lines must appear.</summary>
public static class VmProjects
{
    public static IEnumerable<Test> All(Site site)
    {
        // The native extensions of the tests are built with the C compiler of the OS as shared libraries (.so)
        if (!site.Linux) yield break;
        foreach (var dir in Directory.GetDirectories(site.Path_("tests", "vm")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            yield return new Test("vm." + name, ["vm", "tools"], run => One(run, dir));
        }
    }

    private static async Task One(Run run, string source)
    {
        var site = run.Site;
        await Prepare.Lub(run);
        var dir = Path.Combine(run.Temp, "project");
        CopyTree(source, dir);
        var game = Path.Combine(dir, "game");
        // The C of a native extension is the lowercase .c next to <Name>.ref.cs, built into a shared library with the name of the reference
        foreach (var reference in Directory.Exists(game) ? Directory.GetFiles(game, "*.ref.cs") : [])
        {
            var stem = reference[..^".ref.cs".Length];
            var c = Path.Combine(game, Path.GetFileName(stem).ToLowerInvariant() + ".c");
            if (!File.Exists(c)) continue;
            await run.Must(Environment.GetEnvironmentVariable("CC") ?? "cc",
                ["-shared", "-fPIC", "-O2", "-I" + site.Path_("include"), "-I" + site.Path_("src", "tcs-vm"), c, "-o", stem + ".so"]);
        }
        var env = await Prepare.TcsEnv(run);
        env["GAME_CS"] = Path.Combine(game, "Game.cs");
        env["LUB_CS_LIB"] = site.Path_("cs-lib");
        env["SDL_VIDEO_DRIVER"] = Environment.GetEnvironmentVariable("SDL_VIDEO_DRIVER") ?? "offscreen";
        var envFile = Path.Combine(dir, "env");
        foreach (var line in File.Exists(envFile) ? File.ReadAllLines(envFile) : [])
        {
            int eq = line.IndexOf('=');
            if (eq > 0) env[line[..eq]] = line[(eq + 1)..];
        }
        var output = await run.Exec(site.Lub, [Path.Combine(game, "Game.csproj")], env, timeout: TimeSpan.FromMinutes(3));
        var crash = Path.Combine(dir, "expect-crash.txt");
        var expect = Path.Combine(dir, "expect.txt");
        string? wanted = File.Exists(crash) ? crash : File.Exists(expect) ? expect : null;
        if (File.Exists(crash)) run.Check(!output.Ok, "did not crash");
        else run.Check(output.Ok, "lub exited with " + output.ExitCode);
        var missing = (wanted == null ? [] : File.ReadAllLines(wanted)).Where(want => !output.Text.Contains(want, StringComparison.Ordinal)).ToList();
        run.Check(missing.Count == 0, "missing in the output: " + string.Join(" ", missing.Select(m => "[" + m + "]")));
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
