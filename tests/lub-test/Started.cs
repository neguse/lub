using System.Diagnostics;
using System.Text;

namespace LubTest;

/// <summary>A process a test started and has not waited for (Run.Start).</summary>
public sealed class Started
{
    private readonly Process _process;
    private readonly StringBuilder _text = new();
    private readonly StringBuilder _stdout = new();

    public Started(Process process, Action<string> log)
    {
        _process = process;
        void Take(string? line, bool isStdout)
        {
            if (line == null) return;
            lock (_text)
            {
                _text.Append(line).Append('\n');
                if (isStdout) _stdout.Append(line).Append('\n');
            }
            log(line);
        }
        process.OutputDataReceived += (_, e) => Take(e.Data, true);
        process.ErrorDataReceived += (_, e) => Take(e.Data, false);
        process.Start();
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    public bool Running => !_process.HasExited;

    /// <summary>stdout and stderr so far, in the order they came.</summary>
    public string Text { get { lock (_text) return _text.ToString(); } }

    /// <summary>stdout so far.</summary>
    public string Stdout { get { lock (_text) return _stdout.ToString(); } }

    /// <summary>Waits until the process ends, at most this long. false if it is still running.</summary>
    public async Task<bool> Wait(TimeSpan limit)
    {
        using var cancel = new CancellationTokenSource(limit);
        try
        {
            await _process.WaitForExitAsync(cancel.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    public int ExitCode => _process.ExitCode;

    /// <summary>Ends only this process, at once, without giving it a chance to clean up (SIGKILL, or TerminateProcess on Windows). Its children are left to themselves.</summary>
    public void Kill()
    {
        try { _process.Kill(entireProcessTree: false); } catch (InvalidOperationException) { }
    }
}
