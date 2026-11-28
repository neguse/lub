using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LubTest.Tests;

/// <summary>The tests/cs tests meant for iOS, run in the player app on the iOS simulator (build-ios/lub.app, built by scripts/build-ios.sh before the tests; the macOS lub too).
/// simctl does not report the exit status of the app, so the app writes it to the file named by LUB_EXIT_FILE, and how the app ended is read from the log of SpringBoard.
/// Captures are compared with the Linux goldens within a tolerance, as on macOS. They size the Metal drawable to the requested ConfigOpts dimensions (default 1280x720)
/// rather than Retina window pixels, so the comparison does not depend on the display scaling.
/// The app has no shader compiler: its shaders come from the caches that `lub export ios` writes, one snapshot per test.
/// The app plays audio with AVAudioSession ambient at 48 kHz.</summary>
public static class Ios
{
    private const string Bundle = "dev.neguse.lub";
    // One simulator, one app: the tests run one at a time
    private static readonly SemaphoreSlim Simulator = new(1, 1);
    private static string? _device;
    private static string? _data;

    public static IEnumerable<Test> All(Site site)
    {
        if (!site.Mac) yield break;
        using var doc = JsonDocument.Parse(File.ReadAllText(site.Path_("tests", "cs", "tests.json")));
        var ios = doc.RootElement.GetProperty("ios");
        string[] Names(string key) => [.. ios.GetProperty(key).EnumerateArray().Select(e => e.GetString()!)];
        string[] capture = Names("capture"), exit = Names("exit");
        foreach (var name in capture)
            yield return new Test($"ios.{name}.capture", ["ios"], run => OnSimulator(run, [.. capture, .. exit], () => Capture(run, name))) { Timeout = TimeSpan.FromHours(1) };
        foreach (var name in exit)
            yield return new Test($"ios.{name}", ["ios"], run => OnSimulator(run, [.. capture, .. exit], () => ExitCode(run, name))) { Timeout = TimeSpan.FromHours(1) };
    }

    private static async Task OnSimulator(Run run, string[] all, Func<Task> test)
    {
        await Simulator.WaitAsync();
        try
        {
            await run.Site.Need("ios-app", () => Install(run, all));
            await test();
        }
        finally
        {
            Simulator.Release();
        }
    }

    private static Task<Output> Simctl(Run run, params string[] args) => run.Exec("xcrun", ["simctl", .. args], timeout: TimeSpan.FromMinutes(20));

    /// <summary>Puts the test module, the files the tests read and the shader caches into the app, boots a simulator and installs the app.</summary>
    private static async Task Install(Run run, string[] tests)
    {
        var site = run.Site;
        await Prepare.Lub(run);
        await Prepare.CsTests(run);
        var app = site.Path_("build-ios", "lub.app");
        run.Check(Directory.Exists(app), app + " is missing (build it with scripts/build-ios.sh)");
        var cache = Path.Combine(app, "cache", "shader");
        if (Directory.Exists(Path.Combine(app, "cache"))) Directory.Delete(Path.Combine(app, "cache"), true);
        Directory.CreateDirectory(cache);
        foreach (var name in tests)
        {
            var snapshot = Path.Combine(run.Temp, "ios-" + name);
            await run.Must(site.Lub, ["export", "ios", Prepare.AllTcsb(site), "--entry", name, "-o", snapshot]);
            var shaders = Path.Combine(snapshot, "cache", "shader");
            foreach (var file in Directory.Exists(shaders) ? Directory.GetFiles(shaders) : [])
                File.Copy(file, Path.Combine(cache, Path.GetFileName(file)), overwrite: true);
        }
        Directory.CreateDirectory(Path.Combine(app, "tests", "cs", ".lub"));
        File.Copy(Prepare.AllTcsb(site), Path.Combine(app, "tests", "cs", ".lub", "all.tcsb"), overwrite: true);
        CopyTree(site.Path_("tests", "cs", "data"), Path.Combine(app, "tests", "cs", "data"));

        // The newest iOS runtime that has an iPhone
        var listed = await Simctl(run, "list", "devices", "available", "-j");
        using var devices = JsonDocument.Parse(listed.Stdout);
        static int[] Version(string runtime) => [.. runtime[(runtime.LastIndexOf("iOS-", StringComparison.Ordinal) + 4)..].Split('-').Select(int.Parse)];
        var runtimes = devices.RootElement.GetProperty("devices").EnumerateObject().Where(r => r.Name.Contains("SimRuntime.iOS-", StringComparison.Ordinal))
            .OrderByDescending(r => Version(r.Name), Comparer<int[]>.Create((a, b) => a.Zip(b).Select(p => p.First.CompareTo(p.Second)).FirstOrDefault(c => c != 0, a.Length.CompareTo(b.Length))));
        foreach (var runtime in runtimes)
        {
            foreach (var device in runtime.Value.EnumerateArray())
            {
                if (_device == null && device.GetProperty("name").GetString()!.StartsWith("iPhone", StringComparison.Ordinal)) _device = device.GetProperty("udid").GetString();
            }
        }
        run.Check(_device != null, "no iPhone simulator is available");
        // Booting is not overlapped with the builds: on a fresh runner it keeps the CPU busy for minutes
        await Simctl(run, "boot", _device!); // fails when it is already booted
        var booted = await Simctl(run, "bootstatus", _device!, "-b");
        run.Check(booted.Ok, "the simulator did not boot");
        var installed = await Simctl(run, "install", _device!, app);
        run.Check(installed.Ok, "the app was not installed");
        var container = await Simctl(run, "get_app_container", _device!, Bundle, "data");
        run.Check(container.Ok, "the data container of the app was not found");
        _data = container.Stdout.Trim();
    }

    private static async Task Capture(Run run, string name)
    {
        var png = Path.Combine(_data!, "tmp", name + ".png");
        File.Delete(png);
        await Launch(run, new Dictionary<string, string?>(), [Entry(), "--entry", name, "--capture", png, "--capture-frame", "30", "--fixed-dt", CsRuntime.FixedDt], null);
        run.Check(File.Exists(png), "no capture was written");
        var golden = run.Site.Path_("tests", "golden", name + "_sdlgpu.png");
        var (same, metrics) = PngDiff.Compare(png, golden);
        run.Log(metrics);
        run.Check(same, $"{png} differs from {golden}: {metrics}");
    }

    private static async Task ExitCode(Run run, string name)
    {
        var mark = Path.Combine(_data!, "tmp", name + ".exit");
        File.Delete(mark);
        await Launch(run, new Dictionary<string, string?> { ["SIMCTL_CHILD_LUB_EXIT_FILE"] = mark }, [Entry(), "--entry", name], mark);
        run.Check(File.Exists(mark) && File.ReadAllText(mark).Trim() == "0", "the test did not write exit code 0");
    }

    // The module inside the app
    private static string Entry() => "tests/cs/.lub/all.tcsb";

    /// <summary>Launches the app and waits until it ends. How it ended is only in the log of SpringBoard ("Process exited: ... voluntary", or the signal or watchdog that ended it):
    /// the test fails unless the app exited by itself. On failure, what is known about the launch goes to the log.</summary>
    private static async Task Launch(Run run, Dictionary<string, string?> env, string[] args, string? mark)
    {
        env["SIMCTL_CHILD_LUB_BACKEND"] = "metal";
        var since = DateTime.Now;
        string? failure = null;
        Output? launched = null;
        string exitLine = "";
        try
        {
            launched = await run.Exec("xcrun", ["simctl", "launch", "--console-pty", _device!, Bundle, .. args], env, timeout: TimeSpan.FromMinutes(2));
        }
        catch (TestFailed)
        {
            await Simctl(run, "terminate", _device!, Bundle);
            failure = "timed out";
        }
        if (launched != null)
        {
            var pid = Regex.Match(launched.Text, Regex.Escape(Bundle) + @": ([0-9]+)");
            if (!pid.Success) failure = "the app did not launch";
            else
            {
                for (int i = 0; i < 5 && exitLine == ""; i++)
                {
                    exitLine = await Log(run, since, $"process == \"SpringBoard\" AND eventMessage CONTAINS \"Process exited\" AND eventMessage CONTAINS \"<{Bundle}>:{pid.Groups[1].Value}\"");
                    if (exitLine == "") await Task.Delay(1000);
                }
                if (exitLine == "") run.Log("SpringBoard did not report how pid " + pid.Groups[1].Value + " ended");
                else if (!exitLine.Contains("voluntary", StringComparison.Ordinal)) failure = "the app did not exit by itself";
            }
        }
        if (failure == null) return;
        // What is known about a failed launch: how it ended, what the app and the system logged about it, and any crash report
        run.Log("exit: " + exitLine);
        if (mark != null) run.Log("marker: " + (File.Exists(mark) ? File.ReadAllText(mark).Trim() : "none (the test did not finish)"));
        run.Log("app log:\n" + await Log(run, since, "process == \"lub\"", 40));
        var words = string.Join(" OR ", new[] { "exit", "terminat", "watchdog", "kill", "crash", "jetsam" }.Select(w => $"eventMessage CONTAINS[c] \"{w}\""));
        run.Log("system log about the app:\n" + await Log(run, since, $"(process == \"SpringBoard\" OR process == \"runningboardd\") AND eventMessage CONTAINS \"{Bundle}\" AND ({words})", 40));
        var reports = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Logs", "DiagnosticReports");
        foreach (var report in Directory.Exists(reports) ? Directory.GetFiles(reports, "lub*").Where(f => File.GetLastWriteTime(f) >= since) : [])
            run.Log("crash report " + report + ":\n" + string.Join("\n", File.ReadLines(report).Take(60)));
        run.Fail(failure);
    }

    /// <summary>The simulator's log entries since a time that match a predicate: only the lines that start with a timestamp (not the header of `log`, nor the rest of a multi-line message).</summary>
    private static async Task<string> Log(Run run, DateTime since, string predicate, int last = int.MaxValue)
    {
        var shown = await Simctl(run, "spawn", _device!, "log", "show", "--style", "compact", "--start", since.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture), "--predicate", predicate);
        var lines = shown.Text.Split('\n').Where(l => Regex.IsMatch(l, @"^[0-9]{4}-[0-9]{2}-[0-9]{2} ")).ToList();
        return string.Join("\n", lines.Skip(Math.Max(0, lines.Count - last)));
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(from)) CopyTree(dir, Path.Combine(to, Path.GetFileName(dir)));
    }
}
