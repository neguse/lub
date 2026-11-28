namespace LubTest.Tests;

/// <summary>lider (lider/README.md): its own player, the runtime tests of lider (lider/tests/&lt;name&gt;/) and the tests of the editor core (lider/tests/editor/).</summary>
public static class Lider
{
    public static IEnumerable<Test> All(Site site)
    {
        if (!site.Linux) yield break;
        foreach (var dir in Directory.GetDirectories(site.Path_("lider", "tests")).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name == "editor") continue;
            yield return new Test("lider." + name, ["lider"], async run =>
            {
                var env = await Player(run);
                var output = await run.Script("lider/scripts/test.sh", [name], env, TimeSpan.FromMinutes(15));
                // tests/ime prints `skip` without the kana-kanji conversion dictionary (lider/data/mozc.dict, made by lider/scripts/build-dict.sh)
                run.Check(output.Text.Split('\n').Any(l => l.StartsWith("ok " + name, StringComparison.Ordinal) || l.StartsWith("skip " + name, StringComparison.Ordinal)),
                    "the test did not print `ok " + name + "`");
            });
        }
        // The editor core tests do not run lider
        yield return new Test("lider.editor", ["lider"], async run => await run.Script("lider/scripts/test-editor.sh", [], await Player(run), TimeSpan.FromMinutes(30)));
    }

    /// <summary>Builds the lider player (needs cargo) and returns the environment of the lider scripts.</summary>
    private static async Task<Dictionary<string, string?>> Player(Run run)
    {
        await Prepare.Target(run, "all");
        var env = await Prepare.TcsEnv(run);
        env["LUB_BUILD"] = run.Site.Build;
        await run.Site.Need("lider-player", () => run.Script("lider/scripts/build-player.sh", [], env, TimeSpan.FromHours(1)));
        return env;
    }
}
