using System.Diagnostics;
using System.Text;

namespace LubTest;

/// <summary>What a process run gave back: Text is stdout and stderr together in the order they came, Stdout is stdout alone.</summary>
public sealed record Output(int ExitCode, string Text, string Stdout)
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
        var stdout = new StringBuilder();
        using var process = new Process { StartInfo = info };
        void Take(string? line, bool isStdout)
        {
            if (line == null) return;
            lock (text)
            {
                text.Append(line).Append('\n');
                if (isStdout) stdout.Append(line).Append('\n');
            }
            Log(line);
        }
        process.OutputDataReceived += (_, e) => Take(e.Data, true);
        process.ErrorDataReceived += (_, e) => Take(e.Data, false);
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
        lock (text) return new Output(process.ExitCode, text.ToString(), stdout.ToString());
    }

    /// <summary>Starts a process and returns at once. The caller waits for it or kills it. Its output goes to the log and to Started.Text.</summary>
    public Started Start(string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? cwd = null)
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
        Log("$ (started) " + exe + " " + string.Join(" ", info.ArgumentList));
        var started = new Started(new Process { StartInfo = info }, Log);
        cancel.Register(started.Kill);
        return started;
    }

    /// <summary>Exec, and the test fails unless the process exits with 0.</summary>
    public async Task<Output> Must(string exe, IEnumerable<string> args, IDictionary<string, string?>? env = null, string? cwd = null,
        TimeSpan? timeout = null)
    {
        var output = await Exec(exe, args, env, cwd, timeout);
        if (!output.Ok) Fail(Path.GetFileName(exe) + " exited with " + output.ExitCode);
        return output;
    }

    /// <summary>Runs tcs (the .NET tcs, built once) with the given arguments.</summary>
    public async Task<Output> Tcs(IEnumerable<string> args, string? cwd = null)
    {
        var tcs = await Prepare.Tcs(this);
        return await Exec(tcs[0], [.. tcs[1..], .. args], cwd: cwd, timeout: TimeSpan.FromMinutes(20));
    }

    /// <summary>Tcs, and the test fails unless tcs exits with 0.</summary>
    public async Task<Output> MustTcs(IEnumerable<string> args, string? cwd = null)
    {
        var output = await Tcs(args, cwd);
        if (!output.Ok) Fail("tcs exited with " + output.ExitCode);
        return output;
    }

    /// <summary>Runs a program that opens a window where there may be no display, on the CPU rasterizer of the OS so that what it draws does not depend on the machine.
    /// Linux: Mesa lavapipe, and an X server of its own (Xvfb) unless a display is already there. Windows: WARP. macOS: the real GPU.</summary>
    public async Task<Output> Headless(string exe, IEnumerable<string> args, string? backend = null, IDictionary<string, string?>? env = null,
        string? cwd = null, TimeSpan? timeout = null)
    {
        var all = new Dictionary<string, string?>(env ?? new Dictionary<string, string?>());
        if (backend != null) all["LUB_BACKEND"] = backend;
        // A failed SDL assertion would otherwise sit in a message box nobody clicks
        if (!all.ContainsKey("SDL_ASSERT") && Environment.GetEnvironmentVariable("SDL_ASSERT") == null) all["SDL_ASSERT"] = "abort";
        timeout ??= TimeSpan.FromMinutes(10);
        var (program, arguments) = Site.Headless(exe, [.. args], all);
        return await Exec(program, arguments, all, cwd, timeout);
    }
}
