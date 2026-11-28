using System.Diagnostics;
using System.Text;

namespace LubTest;

/// <summary>What a process run gave back.</summary>
public sealed record Output(int ExitCode, string Text)
{
    public bool Ok => ExitCode == 0;
}

/// <summary>One execution of a test: its log, its temporary directory, and the ways to start processes.</summary>
public sealed class Run(Site site, Test test, CancellationToken cancel)
{
    public Site Site { get; } = site;
    public Test Test { get; } = test;
    private readonly StringBuilder _log = new();
    private string? _temp;

    public string LogText { get { lock (_log) return _log.ToString(); } }

    public void Log(string line)
    {
        lock (_log) _log.Append(line).Append('\n');
    }

    public void Fail(string message) => throw new TestFailed(message);

    public void Check(bool ok, string message)
    {
        if (!ok) Fail(message);
    }

    /// <summary>A directory for this test's files. Removed when the test passes, kept (and named in the report) when it fails.</summary>
    public string Temp
    {
        get
        {
            if (_temp == null)
            {
                _temp = Path.Combine(Site.Logs, Sanitize(Test.Name));
                Directory.CreateDirectory(_temp);
            }
            return _temp;
        }
    }

    public string? TempIfMade => _temp;

    private static string Sanitize(string name) => string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_'));

    /// <summary>Starts a process, waits for it, and returns its exit code and its output (stdout and stderr together, also added to the log).</summary>
    public async Task<Output> Exec(string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? cwd = null,
        TimeSpan? timeout = null)
    {
        var info = new ProcessStartInfo(exe)
        {
            WorkingDirectory = cwd ?? Site.Root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (var a in args) info.ArgumentList.Add(a);
        foreach (var (key, value) in env ?? new Dictionary<string, string?>())
        {
            if (value == null) info.Environment.Remove(key);
            else info.Environment[key] = value;
        }
        Log("$ " + string.Join(" ", (env ?? new Dictionary<string, string?>()).Select(kv => kv.Key + "=" + kv.Value))
            + (env is { Count: > 0 } ? " " : "") + exe + " " + string.Join(" ", info.ArgumentList));
        var text = new StringBuilder();
        using var process = new Process { StartInfo = info };
        void Take(string? line)
        {
            if (line == null) return;
            lock (text) text.Append(line).Append('\n');
            Log(line);
        }
        process.OutputDataReceived += (_, e) => Take(e.Data);
        process.ErrorDataReceived += (_, e) => Take(e.Data);
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new TestFailed("cannot start " + exe + ": " + e.Message);
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout is { } t) limit.CancelAfter(t);
        try
        {
            await process.WaitForExitAsync(limit.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            await process.WaitForExitAsync(CancellationToken.None);
            throw new TestFailed((cancel.IsCancellationRequested ? "the test timed out during: " : "timed out: ") + exe + " " + string.Join(" ", info.ArgumentList));
        }
        // The readers finish after the exit
        process.WaitForExit();
        string result;
        lock (text) result = text.ToString();
        return new Output(process.ExitCode, result);
    }

    /// <summary>Exec, and the test fails unless the process exits with 0.</summary>
    public async Task<Output> Must(string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? cwd = null,
        TimeSpan? timeout = null)
    {
        var output = await Exec(exe, args, env, cwd, timeout);
        if (!output.Ok) Fail(Path.GetFileName(exe) + " exited with " + output.ExitCode);
        return output;
    }

    /// <summary>A bash script of the repo (bash is required on every OS; on Windows it is the one of Git).</summary>
    public Task<Output> Script(string script, IEnumerable<string> args, IDictionary<string, string?>? env = null, TimeSpan? timeout = null) =>
        Must(Site.Bash, [Site.ForScript(Path.Combine(Site.Root, script)), .. args], env, timeout: timeout);

    /// <summary>Runs a program that opens a window where there may be no display, on the CPU rasterizer of the OS so that what it draws does not depend on the machine.
    /// Linux: scripts/run-headless.sh (Mesa lavapipe, and an X server of its own unless a display is already there). Windows: WARP. macOS: the real GPU.</summary>
    public async Task<Output> Headless(string exe, IEnumerable<string> args, string? backend = null, IDictionary<string, string?>? env = null,
        string? cwd = null, TimeSpan? timeout = null)
    {
        var all = new Dictionary<string, string?>(env ?? new Dictionary<string, string?>());
        if (backend != null) all["LUB_BACKEND"] = backend;
        // A failed SDL assertion would otherwise sit in a message box nobody clicks
        if (!all.ContainsKey("SDL_ASSERT") && Environment.GetEnvironmentVariable("SDL_ASSERT") == null) all["SDL_ASSERT"] = "abort";
        timeout ??= TimeSpan.FromMinutes(10);
        if (Site.Windows)
        {
            all.TryAdd("LUB_D3D12_WARP", "1");
            return await Exec(exe, args, all, cwd, timeout);
        }
        if (Site.Mac) return await Exec(exe, args, all, cwd, timeout);
        // scripts/run-headless.sh picks lavapipe and where the window goes. Its Xvfb gets a display number of this run's own,
        // because concurrent xvfb-run -a calls can pick the same number
        all["LUB_XVFB_SERVERNUM"] = Site.TakeDisplay().ToString();
        return await Exec(Site.Bash, [Path.Combine(Site.Root, "scripts", "run-headless.sh"), Path.GetFullPath(exe, cwd ?? Site.Root), .. args], all, cwd, timeout);
    }
}
