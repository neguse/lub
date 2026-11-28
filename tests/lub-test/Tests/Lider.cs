using System.Text.RegularExpressions;

namespace LubTest.Tests;

/// <summary>lider (lider/README.md) on its own player (lider/build/player/lider_player, built by lider/scripts/build-player.sh before the tests):
/// the runtime tests (lider/tests/&lt;name&gt;/), whose entry pulls in the lider sources and runs lider itself, and the tests of the editor core (lider/tests/editor/&lt;name&gt;/), which do not run lider.
/// A test rewrites and writes files, so it runs in a directory of its own.</summary>
public static class Lider
{
    public static IEnumerable<Test> All(Site site)
    {
        if (!site.Linux) yield break;
        var lider = site.Path_("lider");
        foreach (var dir in Directory.GetDirectories(Path.Combine(lider, "tests")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name == "editor") continue;
            yield return new Test("lider." + name, ["lider"], run => Runtime(run, dir));
        }
        foreach (var dir in Directory.GetDirectories(Path.Combine(lider, "tests", "editor")).Order(StringComparer.Ordinal))
            yield return new Test("lider.editor." + Path.GetFileName(dir), ["lider"], run => Editor(run, dir));
    }

    private static string Player(Run run)
    {
        var player = run.Site.Path_("lider", "build", "player", "lider_player");
        run.Check(File.Exists(player), player + " is missing (build it with lider/scripts/build-player.sh)");
        return player;
    }

    /// <summary>The kana-kanji conversion dictionary (not in the repository: lider/scripts/build-dict.sh makes it).</summary>
    private static string Dictionary(Run run) => Environment.GetEnvironmentVariable("JAMO_DICT") is { Length: > 0 } set ? set : run.Site.Path_("lider", "data", "mozc.dict");

    private static async Task<Dictionary<string, string?>> Env(Run run)
    {
        var env = await Prepare.TcsEnv(run);
        // The lider player is not next to the lub executable, so it is told where the cs-lib of lub is
        env["LUB_CS_LIB"] = run.Site.Path_("cs-lib");
        env["SDL_VIDEO_DRIVER"] = Environment.GetEnvironmentVariable("SDL_VIDEO_DRIVER") ?? "offscreen";
        return env;
    }

    private static async Task Runtime(Run run, string source)
    {
        var lider = run.Site.Path_("lider");
        var player = Player(run);
        var dict = Dictionary(run);
        if (Path.GetFileName(source) == "ime") run.Check(File.Exists(dict), dict + " is missing (make it with lider/scripts/build-dict.sh lider/data)");
        var dir = Path.Combine(run.Temp, "test");
        CopyTree(source, dir);
        // The references point to the lider directory (the csproj of the app that the test opens is one directory below)
        foreach (var project in Directory.GetFiles(dir, "*.csproj", SearchOption.AllDirectories).Where(p => Path.GetRelativePath(dir, p).Split(Path.DirectorySeparatorChar).Length <= 2))
            File.WriteAllText(project, Regex.Replace(File.ReadAllText(project), @"(\.\./)+src/Lider\.csproj", Path.Combine(lider, "src", "Lider.csproj")));
        var entry = Path.GetFileName(Directory.GetFiles(dir, "*.csproj").Order(StringComparer.Ordinal).First());
        // The game that lider opens (if any), and the file that the test opens in a window and rewrites (in game/ when it is part of the game)
        var game = Path.Combine(dir, "game", "Game.csproj");
        var target = File.Exists(Path.Combine(dir, "game", "Target.cs")) ? Path.Combine(dir, "game", "Target.cs") : Path.Combine(dir, "Target.cs");
        var env = await Env(run);
        env["LIDER_ROOT"] = dir;
        env["LIDER_GAME"] = File.Exists(game) ? game : "";
        env["LIDER_HOME"] = lider;
        env["LIDER_ACTIVE"] = "1";
        env["LIDER_JAMO_DICT"] = File.Exists(dict) ? dict : "";
        env["LIDER_TEST_FILE"] = target;
        env["LIDER_JAMO_USER"] = Path.Combine(dir, "jamo-user.dat");
        env["LIDER_KEYS"] = Path.Combine(dir, "keys.txt");
        var output = await run.Exec(player, [entry], env, cwd: dir, timeout: TimeSpan.FromMinutes(3));
        run.Check(output.Ok, "the player exited with " + output.ExitCode);
    }

    private static async Task Editor(Run run, string source)
    {
        var site = run.Site;
        var lider = site.Path_("lider");
        var player = Player(run);
        var entry = Path.GetFileNameWithoutExtension(Directory.GetFiles(source, "Test*.cs").Order(StringComparer.Ordinal).First());
        var dir = run.Temp;
        var module = Path.Combine(dir, "test.tcsb");
        // The test, the shared test code, the editor core and what it uses of lider, and the implementation sources of cs-lib
        string[] sources =
        [
            .. Directory.GetFiles(source, "*.cs").Order(StringComparer.Ordinal),
            Path.Combine(lider, "tests", "editor", "EditorTest.cs"),
            .. Directory.GetFiles(Path.Combine(lider, "src", "Editor"), "*.cs").Order(StringComparer.Ordinal),
            Path.Combine(lider, "src", "Core", "Utf8.cs"), Path.Combine(lider, "src", "Core", "Keys.cs"), Path.Combine(lider, "src", "Disk.cs"),
            .. Directory.GetFiles(site.Path_("cs-lib"), "*.cs", SearchOption.AllDirectories).Where(f => Path.GetFileName(f) != "lub_stub.cs").Order(StringComparer.Ordinal),
        ];
        await run.MustTcs(["vm", .. sources, "--ref", site.Path_("cs-lib", "lub_stub.cs"), "--ref", Path.Combine(lider, "src", "Jamo.ref.cs"),
            "--ref", Path.Combine(lider, "src", "Fs.ref.cs"), "--export", entry + ".*", "-o", module]);
        var env = await Env(run);
        env["EDITOR_TEST_DIR"] = dir;
        env["LIDER_JAMO_USER"] = Path.Combine(dir, "jamo-user.dat");
        env["LIDER_JAMO_DICT"] = Dictionary(run);
        var output = await run.Exec(player, [module, "--entry", entry], env, cwd: dir, timeout: TimeSpan.FromMinutes(2));
        run.Check(output.Ok, "the player exited with " + output.ExitCode);
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
