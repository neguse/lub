using System;
using System.IO;
using System.Text;

// .NET implementation of what tcs asks of the outside about files (the declarations are in Api/TcsFiles.cs; C: Native/files.c)
public static class TcsFiles
{
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

    public static string? Read(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    public static string WriteText(string path, string text)
    {
        try
        {
            Tcs.OutputFileWriter.WriteAllText(path, text);
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return ex.Message;
        }
    }

    // Writes a string with one char per byte (0-255) as bytes in Latin-1 (strings in the C host are raw byte sequences)
    public static string WriteBytes(string path, string bytes)
    {
        try
        {
            Tcs.OutputFileWriter.WriteAllBytes(path, System.Text.Encoding.Latin1.GetBytes(bytes));
            return "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException
            or NotSupportedException)
        {
            return ex.Message;
        }
    }

    public static string Stamp(string path)
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

    // lib/ is embedded in the assembly, and runtime/ is next to it
    public static string? Shipped(string name)
    {
        if (name.StartsWith("lib/", StringComparison.Ordinal))
        {
            using var stream = typeof(TcsFiles).Assembly.GetManifestResourceStream(name);
            if (stream == null) return null;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
        return Read(Path.Combine(AppContext.BaseDirectory, name));
    }
}
