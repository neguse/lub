using System.Text;

// tcs の host (SelfHost/Tool) の OS の関数の .NET の実装 (C は SelfHost/Native/host.c、wasm は SelfHost/Web/tcs_web.c)
public static class TcsHost
{
    private static string[] _args = [];
    private static int _exitCode;
    private static bool _done;

    // CLI (TcsSelf.Cli) と host の状態は static なので、同じ process で並んで呼ばれたら 1 つずつ動かす (テストは Program.Main を並列に呼ぶ)
    private static readonly object Gate = new();

    // CLI を最後まで動かして終了コードを返す
    public static int Run(string[] args) => Run(args, TcsSelf.Cli.Start, TcsSelf.Cli.Step);

    // TcsHost を使う別の TinyC# の program (lub-gen など) を同じ形で動かす: start を 1 回、SetDone まで step を呼ぶ
    public static int Run(string[] args, Action start, Action step)
    {
        lock (Gate)
        {
            _args = args;
            _exitCode = 0;
            _done = false;
            start();
            while (!_done) step();
            Console.Out.Flush();
            return _exitCode;
        }
    }

    public static int ArgCount() => _args.Length;
    public static string Arg(int index) => _args[index];
    public static bool FileExists(string path) => File.Exists(path);

    // 両方が既存のファイルで、link を全部たどった絶対 path が同じか (Windows と macOS は大文字小文字を区別しない)
    public static bool SameFile(string a, string b)
    {
        try
        {
            if (!File.Exists(a) || !File.Exists(b)) return false;
            var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            return string.Equals(ResolvedPath(a, 0), ResolvedPath(b, 0), comparison);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return false;
        }
    }

    // path の各要素の symbolic link をたどった絶対 path
    private static string ResolvedPath(string path, int depth)
    {
        if (depth > 40) throw new IOException("too many levels of symbolic links: " + path);
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        var current = root;
        var parts = full.Substring(root.Length).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var next = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(next) ? new DirectoryInfo(next) : new FileInfo(next);
            if (info.LinkTarget != null)
            {
                var target = Path.IsPathRooted(info.LinkTarget) ? info.LinkTarget : Path.Combine(current, info.LinkTarget);
                next = ResolvedPath(target, depth + 1);
            }
            current = next;
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    public static string ReadFile(string path)
    {
        try { return File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return ""; }
    }

    public static string WriteFileAtomic(string path, string text)
    {
        try
        {
            TinyCs.OutputFileWriter.WriteAllText(path, text);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return ex.Message;
        }
    }

    // 1 文字が 1 byte (0〜255) の文字列を Latin-1 で byte にして書く (C の host の文字列は byte 列そのもの)
    public static string WriteBinaryAtomic(string path, string bytes)
    {
        try
        {
            TinyCs.OutputFileWriter.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(bytes));
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return ex.Message;
        }
    }

    public static void WriteOut(string text) => Console.Out.Write(text);
    public static void WriteErr(string text) => Console.Error.Write(text);
    public static void FlushOut() => Console.Out.Flush();

    public static string FileStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "";
        }
    }

    public static string ToolFile(string name)
    {
        if (name.StartsWith("lib/", StringComparison.Ordinal))
        {
            using var stream = typeof(TcsHost).Assembly.GetManifestResourceStream(name);
            if (stream == null) return "";
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        var path = Path.Combine(AppContext.BaseDirectory, name);
        return File.Exists(path) ? File.ReadAllText(path) : "";
    }

    public static void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
    public static void SetExitCode(int code) => _exitCode = code;
    public static void SetDone() => _done = true;
}
