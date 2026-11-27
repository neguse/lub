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
//   --update-goldens writes the captures of the golden tests as the new goldens
//   --list           prints the selected tests and their labels without running them
var labels = new List<string>();
var skipLabels = new List<string>();
string? name = null;
string? build = null;
string? logs = null;
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

string root = AppContext.BaseDirectory;
while (!File.Exists(Path.Combine(root, "tcs.slnx")))
    root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("the repo root was not found above " + AppContext.BaseDirectory);
var site = new Site(root, build, jobs, logs) { UpdateGoldens = update };

var all = Catalog.All(site);
var duplicate = all.GroupBy(t => t.Name).FirstOrDefault(g => g.Count() > 1);
if (duplicate != null) throw new InvalidOperationException("two tests are named " + duplicate.Key);
var selected = all
    .Where(t => labels.Count == 0 || t.Labels.Any(labels.Contains))
    .Where(t => !t.Labels.Any(skipLabels.Contains))
    .Where(t => name == null || Regex.IsMatch(t.Name, name))
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
