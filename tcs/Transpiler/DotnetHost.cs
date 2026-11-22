using System.Text;

// .NET implementation of the OS functions of the tcs host (SelfHost/Tool) (C: SelfHost/Native/host.c, wasm: SelfHost/Web/tcs_web.c)
public static class TcsHost
{
    private static string[] _args = [];
    private static int _exitCode;
    private static bool _done;

    // The CLI (TcsSelf.Cli) and host state are static, so concurrent calls in the same process run one at a time (tests call Program.Main in parallel)
    private static readonly object Gate = new();

    // Runs the CLI to completion and returns the exit code
    public static int Run(string[] args) => Run(args, TcsSelf.Cli.Start, TcsSelf.Cli.Step);

    // Runs another TinyC# program that uses TcsHost (lub-gen etc.) the same way: call start once, then step until SetDone
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

    // Whether both are existing files with the same absolute path after following all links (Windows and macOS are case-insensitive)
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

    // Absolute path with the symbolic links of each path element followed
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

    // Writes a string with one char per byte (0-255) as bytes in Latin-1 (strings in the C host are raw byte sequences)
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
