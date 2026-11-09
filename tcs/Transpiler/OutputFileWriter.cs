namespace TinyCs;

internal static class OutputFileWriter
{
    public static void WriteAllText(string path, string contents) =>
        Write(path, stream =>
        {
            using var writer = new StreamWriter(stream);
            writer.Write(contents);
        });

    public static void WriteAllBytes(string path, byte[] bytes) => Write(path, stream => stream.Write(bytes));

    // 一時ファイルに書いてから置き換える (既にあるファイルの mode を保つ)
    private static void Write(string path, Action<Stream> write)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        Directory.CreateDirectory(directory);
        UnixFileMode? existingMode = null;
        if (File.Exists(fullPath))
        {
            using (File.OpenHandle(fullPath, FileMode.Open, FileAccess.Write,
                       FileShare.ReadWrite | FileShare.Delete)) { }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                existingMode = File.GetUnixFileMode(fullPath);
        }
        var temporaryPath = Path.Combine(directory,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using (var stream = new FileStream(temporaryPath,
                       FileMode.CreateNew, FileAccess.Write, FileShare.None))
                write(stream);

            if ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                && existingMode is { } mode)
                File.SetUnixFileMode(temporaryPath, mode);
            File.Move(temporaryPath, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }
}
