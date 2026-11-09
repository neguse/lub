namespace TinyCs.Tests;

// Console.Out/Error はプロセス全域の状態。テストが生の SetOut で差し替えると、
// 並列中の他テスト (SpecDotnetExecutor の in-proc 実行等) のルーティングを
// 一時的に無効化してしまう (CI 2 vCPU で顕在化した空 capture の真因)。
// 全テストはこの AsyncLocal ルーティング経由で capture し、SetOut は
// ここ以外で呼ばない。
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

    /// <summary>この論理フローの Console.Out を writer へ向ける。</summary>
    public static IDisposable BeginOut(StringWriter writer)
    {
        EnsureInstalled();
        OutCapture.Value = writer;
        return new Scope(OutCapture);
    }

    /// <summary>action の間だけ stdout/stderr を捕捉して返す。</summary>
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
