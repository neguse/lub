using System.Diagnostics;

namespace TinyCs.Tests;

internal readonly record struct ProcessResult(
    int ExitCode, string Stdout, string Stderr);

internal sealed class ProcessTimeoutException : TimeoutException
{
    public int ProcessId { get; }
    public string Stdout { get; }
    public string Stderr { get; }

    public ProcessTimeoutException(string command, int processId,
        TimeSpan timeout, string stdout, string stderr,
        string? diagnosticContext = null, Exception? cleanupError = null,
        string phase = "execution")
        : base(BuildMessage(command, processId, timeout, stdout, stderr,
            diagnosticContext, cleanupError, phase), cleanupError)
    {
        ProcessId = processId;
        Stdout = stdout;
        Stderr = stderr;
    }

    private static string BuildMessage(string command, int processId,
        TimeSpan timeout, string stdout, string stderr,
        string? diagnosticContext, Exception? cleanupError, string phase)
    {
        var message = $"Process '{command}' (PID {processId}) timed out " +
                      $"during {phase} after " +
                      $"{timeout.TotalMilliseconds:0} ms.";
        if (stdout.Length > 0)
            message += $"\nstdout:\n{Truncate(stdout)}";
        if (stderr.Length > 0)
            message += $"\nstderr:\n{Truncate(stderr)}";
        if (!string.IsNullOrEmpty(diagnosticContext))
            message += $"\ncontext:\n{Truncate(diagnosticContext)}";
        if (cleanupError != null)
            message += $"\ncleanup error: {cleanupError.Message}";
        return message;
    }

    private static string Truncate(string value) =>
        value.Length <= 2_000 ? value : value[..2_000] + "...";
}

internal static class TestProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(5);

    public static ProcessResult Run(
        ProcessStartInfo startInfo, TimeSpan timeout,
        string? diagnosticContext = null) =>
        RunAsync(startInfo, timeout, diagnosticContext)
            .GetAwaiter().GetResult();

    private static async Task<ProcessResult> RunAsync(
        ProcessStartInfo startInfo, TimeSpan timeout,
        string? diagnosticContext)
    {
        if (!startInfo.RedirectStandardOutput
            || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException(
                "Process output and error must both be redirected.",
                nameof(startInfo));
        }
        if (timeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        var command = DescribeCommand(startInfo);
        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException(
                $"Failed to start process: {startInfo.FileName}");
        var processId = process.Id;
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync().WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            var terminationError = await TerminateAsync(process);
            var (timeoutStdout, timeoutStderr, drainError) =
                await DrainAsync(stdoutTask, stderrTask);
            var cleanupError = CombineErrors(terminationError, drainError);
            throw new ProcessTimeoutException(command, processId,
                timeout, timeoutStdout, timeoutStderr, diagnosticContext,
                cleanupError);
        }

        var (stdout, stderr, outputError) =
            await DrainAsync(stdoutTask, stderrTask);
        if (outputError is TimeoutException)
        {
            throw new ProcessTimeoutException(command, processId,
                CleanupTimeout, stdout, stderr, diagnosticContext,
                outputError, "output drain");
        }
        if (outputError != null)
        {
            throw new InvalidOperationException(
                $"Failed to drain output from process '{command}' " +
                $"(PID {processId}).", outputError);
        }
        return new ProcessResult(process.ExitCode, stdout, stderr);
    }

    private static async Task<Exception?> TerminateAsync(Process process)
    {
        Exception? cleanupError = null;
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is SystemException
            or AggregateException)
        {
            cleanupError = ex;
        }

        try
        {
            await process.WaitForExitAsync().WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (ex is SystemException
            or AggregateException)
        {
            cleanupError ??= ex;
        }
        return cleanupError;
    }

    private static async Task<(string Stdout, string Stderr, Exception? Error)>
        DrainAsync(
        Task<string> stdoutTask, Task<string> stderrTask)
    {
        Exception? error = null;
        try
        {
            await Task.WhenAll(stdoutTask, stderrTask)
                .WaitAsync(CleanupTimeout);
        }
        catch (Exception ex) when (ex is SystemException
            or AggregateException)
        {
            error = ex;
        }

        return (
            stdoutTask.IsCompletedSuccessfully ? await stdoutTask : "",
            stderrTask.IsCompletedSuccessfully ? await stderrTask : "",
            error);
    }

    private static Exception? CombineErrors(
        Exception? first, Exception? second)
    {
        if (first == null) return second;
        if (second == null) return first;
        return new AggregateException(first, second);
    }

    private static string DescribeCommand(ProcessStartInfo startInfo)
    {
        var arguments = startInfo.ArgumentList.Count > 0
            ? string.Join(" ", startInfo.ArgumentList.Select(QuoteArgument))
            : startInfo.Arguments;
        return string.IsNullOrWhiteSpace(arguments)
            ? startInfo.FileName
            : $"{startInfo.FileName} {arguments}";
    }

    private static string QuoteArgument(string value)
    {
        if (value.Length > 0
            && !value.Any(char.IsWhiteSpace)
            && !value.Contains('"'))
        {
            return value;
        }
        return $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
