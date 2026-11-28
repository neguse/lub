using System.Text.Json;

namespace LubTest.Tests;

/// <summary>The list of the runtime tests in tests/cs (tests/cs/tests.json).</summary>
public sealed class CsList
{
    public string[] Args { get; }
    public string[] Runtime { get; }
    public string[] Visual { get; }
    public HashSet<string> DotnetSkip { get; }

    public CsList(Site site)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(site.Path_("tests", "cs", "tests.json")));
        string[] Names(string key) => [.. doc.RootElement.GetProperty(key).EnumerateArray().Select(e => e.GetString()!)];
        Args = Names("args");
        Runtime = Names("runtime");
        Visual = Names("visual");
        DotnetSkip = [.. doc.RootElement.GetProperty("dotnetSkip").EnumerateObject().Select(p => p.Name)];
    }
}

/// <summary>The runtime tests of tests/cs: lub runs one entry of the module of all of tests/cs, and the exit code is the result.
/// Each runs on every backend of the OS with the tcs VM player, and once on .NET execution (the backend is inside the C runtime, which .NET execution shares).</summary>
public static class CsRuntime
{
    // The frame time that makes a run the same on every machine
    public const string FixedDt = "0.0166666666666667";

    public static IEnumerable<Test> All(Site site)
    {
        var list = new CsList(site);
        foreach (var name in list.Runtime)
        {
            foreach (var backend in site.Backends)
                yield return new Test($"cs.{name}.{backend}", ["cs", "cs-vm", backend], run => OnVm(run, name, backend, list.Args));
            // .NET execution shares the C runtime with the tcs VM player, so it is run on one OS
            if (site.Linux && !list.DotnetSkip.Contains(name))
                yield return new Test($"cs.{name}.dotnet", ["cs", "cs-dotnet"], run => OnDotnet(run, name, list.Args));
        }
    }

    /// <summary>Runs one entry on the tcs VM player and returns the output. Fails unless it exits with 0 (and, on D3D12, unless the debug layer stays silent).</summary>
    public static async Task<Output> OnVm(Run run, string entry, string backend, IEnumerable<string> args, IDictionary<string, string?>? env = null,
        string? cwd = null, string? module = null)
    {
        await Prepare.Lub(run);
        if (module == null) await Prepare.CsTests(run);
        var all = new Dictionary<string, string?>(env ?? new Dictionary<string, string?>());
        // The debug layer of D3D12 reports what the backend did wrong; an error there fails the test
        if (backend == "d3d12") all.TryAdd("LUB_D3D12_DEBUG", "1");
        var output = await run.Headless(run.Site.Lub, [module ?? Prepare.AllTcsb(run.Site), "--entry", entry, .. args], backend, all, cwd);
        run.Check(output.Ok, $"{entry} ({backend}) exited with {output.ExitCode}");
        if (backend == "d3d12")
        {
            run.Check(!System.Text.RegularExpressions.Regex.IsMatch(output.Text, @"d3d12\[[^]]*\] (CORRUPTION|ERROR):"), "the D3D12 debug layer reported errors");
            // A debug layer that did not load reports nothing, and every test would pass unchecked
            if (entry == "TestManyDraws") run.Check(output.Text.Contains("d3d12: debug layer messages: on", StringComparison.Ordinal), "the D3D12 debug layer did not load");
            // The GPU time of each pass is measured with timestamp queries (Sys.TakeGpuPasses)
            if (entry == "TestGpuPassTime") run.Check(output.Text.Contains("gpu pass time: timed", StringComparison.Ordinal), "d3d12 did not time the GPU passes");
        }
        return output;
    }

    /// <summary>Runs one entry on .NET execution and returns the output.</summary>
    public static async Task<Output> OnDotnet(Run run, string entry, IEnumerable<string> args, IDictionary<string, string?>? env = null)
    {
        await Prepare.DotnetCsTests(run);
        var all = new Dictionary<string, string?>(Prepare.NativeLib(run.Site));
        foreach (var (key, value) in env ?? new Dictionary<string, string?>()) all[key] = value;
        var output = await run.Headless(Prepare.DotnetTests(run.Site), ["--entry", entry, .. args], run.Site.Backend, all);
        run.Check(output.Ok, $"{entry} (.NET) exited with {output.ExitCode}");
        return output;
    }
}
