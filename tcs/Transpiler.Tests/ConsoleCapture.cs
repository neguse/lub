namespace TinyCs.Tests;

// Console.Out/Error are process-wide state. If a test replaces them with a raw SetOut,
// it temporarily disables the routing of other tests running in parallel (in-proc runs of SpecDotnetExecutor, etc.)
// (the root cause of the empty captures that showed up on CI with 2 vCPUs).
// All tests capture through this AsyncLocal routing, and SetOut is
// not called anywhere else.
internal static class ConsoleCapture
{
    private static readonly AsyncLocal<StringWriter?> OutCapture = new();
    private static readonly AsyncLocal<StringWriter?> ErrorCapture = new();
    private static readonly object InstallLock = new();
    private static bool _installed;

    public static void EnsureInstalled()
    {
        if (_installed) return;
        lock (InstallLock)
        {
            if (_installed) return;
            Console.SetOut(new Router(Console.Out, OutCapture));
            Console.SetError(new Router(Console.Error, ErrorCapture));
            _installed = true;
        }
    }

    /// <summary>Points Console.Out of this logical flow to writer.</summary>
    public static IDisposable BeginOut(StringWriter writer)
    {
        EnsureInstalled();
        OutCapture.Value = writer;
        return new Scope(OutCapture);
    }

    /// <summary>Captures stdout/stderr only while action runs and returns them.</summary>
    public static (int ExitCode, string Stdout, string Stderr) Run(
        Func<int> action)
    {
        EnsureInstalled();
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        OutCapture.Value = stdout;
        ErrorCapture.Value = stderr;
        try
        {
            var exitCode = action();
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            OutCapture.Value = null;
            ErrorCapture.Value = null;
        }
    }

    private sealed class Scope(AsyncLocal<StringWriter?> slot) : IDisposable
    {
        public void Dispose() => slot.Value = null;
    }

    private sealed class Router(TextWriter fallback,
        AsyncLocal<StringWriter?> capture) : TextWriter
    {
        public override System.Text.Encoding Encoding => fallback.Encoding;
        private TextWriter Target => capture.Value ?? fallback;
        public override void Write(char value) => Target.Write(value);
        public override void Write(string? value) => Target.Write(value);
        public override void WriteLine(string? value) =>
            Target.WriteLine(value);
    }
}
