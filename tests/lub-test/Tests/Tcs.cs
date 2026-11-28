using System.Text.RegularExpressions;

namespace LubTest.Tests;

/// <summary>The tests of tcs itself: the dotnet test projects (compiler, backends, analyzer), the C-only checks, the tcs VM tests, and the analyzer on a project with violations.
/// The dotnet test projects are split into shares by the first letter of the class name, so that CI can run the shares as separate jobs; every class is in exactly one share.
/// The expected results are what .NET does (TCS_DIFFERENTIAL, TCS_FUZZ) and what Roslyn accepts (the spec conformance sweep, whose per-chapter report goes to build-tcs-tests/).</summary>
public static class Tcs
{
    private static readonly string[] Tests1 = [.. "ABCDEFGHIJKL".Select(c => c.ToString())];
    private static readonly string[] Backends1 = [.. "ABCDEFG".Select(c => c.ToString())];
    private static readonly string[] Backends2 = [.. "HIJKLMN".Select(c => c.ToString())];

    public static IEnumerable<Test> All(Site site)
    {
        yield return Dotnet("tcs.tests.1", "tests/tcs", In("TinyCs.Tests", Tests1));
        yield return Dotnet("tcs.tests.2", "tests/tcs", Out("TinyCs.Tests", Tests1));
        yield return Dotnet("tcs.analyzers", "tests/tcs-analyzers", null);
        yield return Dotnet("tcs.backends.1", "tests/tcs-backends", In("TinyCs.Tcs2c.Tests", Backends1));
        yield return Dotnet("tcs.backends.2", "tests/tcs-backends", In("TinyCs.Tcs2c.Tests", Backends2));
        yield return Dotnet("tcs.backends.3", "tests/tcs-backends", Out("TinyCs.Tcs2c.Tests", [.. Backends1, .. Backends2]));

        yield return new Test("tcs.samples-check", ["tcs", "tcs-scripts"], async run =>
        {
            var env = await Prepare.TcsEnv(run);
            foreach (var sample in new[] { "hello", "game", "inventory", "entity", "statemachine", "collision" })
                await run.Script("scripts/tcs.sh", ["check", $"tests/tcs/samples/{sample}.cs"], env);
            await run.Script("scripts/tcs.sh", ["check", "tests/tcs/samples/host_api_game.cs", "--ref", "tests/tcs/samples/host_api_stub.cs"], env);
        });
        // The checks that only C can run (tcs c, then the C compiler)
        foreach (var check in new[] { "digests", "gc", "game-core", "object-values", "host" })
        {
            yield return new Test("tcs.c." + check, ["tcs", "tcs-scripts"], async run =>
                await run.Script($"tests/tcs-backends/verify-{check}.sh", [], await Prepare.TcsEnv(run), TimeSpan.FromMinutes(30)));
        }
        // tcs vm, then tcsvm: the expected output, and the same output from C where C can run the input
        yield return new Test("tcs.vm", ["tcs", "tcs-scripts"], async run =>
            await run.Script("tests/tcs-vm/run-tests.sh", [], await Prepare.TcsEnv(run), TimeSpan.FromMinutes(30))) { Alone = true };
        // The analyzer reports each kind of violation in a project written to contain them
        yield return new Test("tcs.analyzer-demo", ["tcs", "tcs-scripts"], async run =>
        {
            var built = await run.Must("dotnet", ["build", "tests/tcs-analyzers-demo/tcs-analyzers-demo.csproj", "--no-incremental"], timeout: TimeSpan.FromMinutes(20));
            var lines = built.Text.Split('\n').Distinct().ToList();
            int Count(string id) => lines.Count(l => l.Contains("warning " + id + ":", StringComparison.Ordinal));
            run.Check(Count("TCS1001") == 5 && Count("TCS1002") == 1 && Count("TCS1003") == 1,
                $"expected TCS1001 x5, TCS1002 x1, TCS1003 x1; got x{Count("TCS1001")}, x{Count("TCS1002")}, x{Count("TCS1003")}");
            foreach (var text in new[] { "StructMember", "LocalFunctionStatement", "TryStatement", "ThrowStatement", "ListPattern", "System.IO.File.ReadAllText",
                         "Dictionary<K,V> cannot store null values" })
                run.Check(built.Text.Contains(text, StringComparison.Ordinal), "the analyzer output has no " + text);
        });

        Test Dotnet(string name, string project, string? filter) => new(name, ["tcs", "tcs-dotnet"], async run =>
        {
            // Built once, so that the shares of one project do not build it at the same time
            await run.Site.Need("dotnet-build:" + project, () => run.Must("dotnet", ["build", project, "--verbosity", "minimal"], timeout: TimeSpan.FromMinutes(30)));
            var env = new Dictionary<string, string?>
            {
                ["TCS_SPEC_CONFORMANCE"] = "1",
                ["TCS_SPEC_REPORT"] = site.Path_("build-tcs-tests", "spec-conformance-report.md"),
                ["TCS_DIFFERENTIAL"] = "1",
                ["TCS_FUZZ"] = "1",
            };
            string[] select = filter == null ? [] : ["--filter", filter];
            var output = await run.Exec("dotnet", ["test", project, "--no-build", "--verbosity", "minimal", .. select], env, timeout: TimeSpan.FromHours(1));
            run.Check(output.Ok, "dotnet test failed");
            run.Check(Regex.IsMatch(output.Text, @"Passed!\s+- Failed:\s+0, Passed:\s+[1-9]"), "dotnet test ran no test");
        })
        { Alone = true, Timeout = TimeSpan.FromHours(1) };
    }

    // Classes directly under the namespace whose name starts with one of the letters
    private static string In(string ns, string[] letters) => string.Join("|", letters.Select(l => $"FullyQualifiedName~{ns}.{l}"));

    // Classes directly under the namespace whose name starts with none of the letters
    private static string Out(string ns, string[] letters) => string.Join("&", letters.Select(l => $"FullyQualifiedName!~{ns}.{l}"));
}
