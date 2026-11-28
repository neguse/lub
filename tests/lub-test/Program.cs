using System.Diagnostics;
using System.Text.RegularExpressions;
using LubTest;

// lub-test: runs the tests of this repo.
//   dotnet run --project tests/lub-test -- [options]
//   --label L        only tests with this label (repeatable: a test with any of them)
//   --skip-label L   no test with this label (repeatable)
//   --name REGEX     only tests whose name matches
//   --shard K/N      the K-th of N shares of the selected tests (CI runs the shares as separate jobs)
//   --jobs N         how many tests run at the same time (default: the number of cores)
//   --build DIR      the build directory of lub (default: build-release-linux, build-mac or build-release, by OS)
//   --logs DIR       where the files of failed tests are kept (default: a directory under the temporary directory)
//   --fuzz-count N   how many inputs the fuzz tests generate (default: a small number for each test; the long sweeps of label fuzz default to 500)
//   --fuzz-seed N    the first seed of the fuzz tests
//   --update-goldens writes the captures of the golden tests as the new goldens
//   --list           prints the selected tests and their labels without running them
// lub-test fake-tcs: stands in for `tcs vm --watch` in the test of how lub reads the frames of its tcs child (Tests/DevTools.cs)
if (args is ["fake-tcs", ..])
{
    using var stdout = Console.OpenStandardOutput();
    var frames = LubTest.Tests.DevTools.FakeTcsOutput();
    stdout.Write(frames, 0, frames.Length);
    stdout.Flush();
    Thread.Sleep(TimeSpan.FromMinutes(10));
    return 0;
}

string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "tcs.slnx")))
    root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("the repo root was not found above " + AppContext.BaseDirectory);

// Tools that share code with the tests, for the scripts and for use by hand:
//   lub-test gen-api                 writes the files generated from the API description (GenApi.cs)
//   lub-test cs-tests                compiles all of tests/cs into tests/cs/.lub/all.tcsb (what lub runs with --entry <Entry>)
//   lub-test headless <exe> [args]   runs a program that opens a window where there may be no display (Site.Headless)
//   lub-test png-diff <a> <b>        compares two captures within the tolerance of the golden tests (PngDiff)
if (args is ["gen-api"] or ["cs-tests"])
{
    var tool = new Site(root, null, 1, null);
    var work = new Run(tool, new Test(args[0], [], _ => Task.CompletedTask), CancellationToken.None);
    try
    {
        if (args[0] == "gen-api") await GenApi.Generate(work, check: false);
        else await Prepare.CsTests(work);
        foreach (var line in work.LogText.Split('\n').Where(l => l.StartsWith("gen-api: ", StringComparison.Ordinal))) Console.WriteLine(line);
        Directory.Delete(tool.Logs, true);
        return 0;
    }
    catch (TestFailed e)
    {
        Console.Error.WriteLine(work.LogText);
        Console.Error.WriteLine(args[0] + ": " + e.Message);
        return 1;
    }
}
if (args is ["headless", var program, .. var rest])
{
    var tool = new Site(root, null, 1, null);
    Directory.Delete(tool.Logs, true);
    var env = new Dictionary<string, string?>();
    // A failed SDL assertion would otherwise sit in a message box nobody clicks
    if (Environment.GetEnvironmentVariable("SDL_ASSERT") == null) env["SDL_ASSERT"] = "abort";
    var (exe, arguments) = tool.Headless(Path.GetFullPath(program), rest, env);
    var info = new ProcessStartInfo(exe);
    foreach (var a in arguments) info.ArgumentList.Add(a);
    foreach (var (key, value) in env)
    {
        if (value != null) info.Environment[key] = value;
    }
    using var child = Process.Start(info)!;
    child.WaitForExit();
    return child.ExitCode;
}
if (args is ["png-diff", var first, var second])
{
    var (same, metrics) = PngDiff.Compare(first, second);
    Console.WriteLine(metrics);
    return same ? 0 : 1;
}

var labels = new List<string>();
var skipLabels = new List<string>();
string? name = null;
string? build = null;
string? logs = null;
int? fuzzCount = null, fuzzSeed = null;
int shardK = 1, shardN = 1;
int jobs = Environment.ProcessorCount;
bool list = false, update = false;
for (int i = 0; i < args.Length; i++)
{
    string Value() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
    switch (args[i])
    {
        case "--label": labels.Add(Value()); break;
        case "--skip-label": skipLabels.Add(Value()); break;
        case "--name": name = Value(); break;
        case "--build": build = Value(); break;
        case "--logs": logs = Value(); break;
        case "--fuzz-count": fuzzCount = int.Parse(Value()); break;
        case "--fuzz-seed": fuzzSeed = int.Parse(Value()); break;
        case "--jobs": jobs = int.Parse(Value()); break;
        case "--shard":
            var parts = Value().Split('/');
            shardK = int.Parse(parts[0]);
            shardN = int.Parse(parts[1]);
            break;
        case "--list": list = true; break;
        case "--update-goldens": update = true; break;
        default:
            Console.Error.WriteLine("unknown option: " + args[i]);
            return 2;
    }
}

var site = new Site(root, build, jobs, logs) { UpdateGoldens = update, FuzzCount = fuzzCount, FuzzSeed = fuzzSeed };

var all = Catalog.All(site);
var duplicate = all.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
if (duplicate != null) throw new InvalidOperationException("two tests are named " + duplicate.Key);
var selected = all
    .Where(t => labels.Count == 0 || t.Labels.Any(labels.Contains))
    .Where(t => !t.Labels.Any(skipLabels.Contains))
    .Where(t => name == null || Regex.IsMatch(t.Name, name))
    .Where(t => !t.OnRequest || labels.Count > 0 || name != null)
    .Where((_, index) => index % shardN == shardK - 1)
    .ToList();
if (list)
{
    foreach (var t in selected) Console.WriteLine(t.Name + "  [" + string.Join(" ", t.Labels) + "]");
    Console.WriteLine(selected.Count + " tests");
    return 0;
}
if (selected.Count == 0)
{
    Console.Error.WriteLine("no test matches");
    return 2;
}

var clock = Stopwatch.StartNew();
var slots = new SemaphoreSlim(jobs, jobs);
var failed = new List<string>();
var output = new object();
async Task One(Test test)
{
    // A test that uses every core by itself waits for all the slots
    int take = test.Alone ? jobs : 1;
    for (int i = 0; i < take; i++) await slots.WaitAsync();
    var started = clock.Elapsed;
    using var cancel = new CancellationTokenSource(test.Timeout);
    var run = new Run(site, test, cancel.Token);
    string? failure = null;
    try
    {
        await test.Body(run);
    }
    catch (TestFailed e)
    {
        failure = e.Message;
    }
    catch (OperationCanceledException)
    {
        failure = "timed out after " + test.Timeout;
    }
    catch (Exception e)
    {
        failure = e.ToString();
    }
    finally
    {
        slots.Release(take);
    }
    double seconds = (clock.Elapsed - started).TotalSeconds;
    lock (output)
    {
        if (failure == null)
        {
            Console.WriteLine($"PASS {test.Name} ({seconds:0.0}s)");
            if (run.TempIfMade is { } temp)
            {
                try { Directory.Delete(temp, true); } catch (IOException) { }
            }
            return;
        }
        failed.Add(test.Name);
        Console.WriteLine($"FAIL {test.Name} ({seconds:0.0}s): {failure}");
        foreach (var line in run.LogText.Split('\n')) Console.WriteLine("    " + line);
        if (run.TempIfMade is { } kept) Console.WriteLine("    files kept in " + kept);
    }
}
await Task.WhenAll(selected.Select(One));
Console.WriteLine($"---\n{selected.Count - failed.Count} passed, {failed.Count} failed, {clock.Elapsed.TotalSeconds:0}s");
if (failed.Count == 0)
{
    try { Directory.Delete(site.Logs, true); } catch (IOException) { }
    return 0;
}
foreach (var f in failed) Console.WriteLine("FAILED " + f);
return 1;
