// Declarations of what tcs asks of the outside about files. The side that runs tcs gives the implementation:
// the C build takes this file as --ref and links Native/files_tcs.c (on the OS functions of Native/files.c), and .NET uses Dotnet/Files.cs.
// This file is not included in the .NET build.
public static class TcsFiles
{
    // The text of a file (UTF-8, without a byte order mark). null if it is not a file or cannot be read
    public static string? Read(string path) { return null; }
    // A file shipped with tcs (`lib/TinySystem/Linq.cs`, `runtime/core/tcs_core.c`, etc.). null if missing
    public static string? Shipped(string name) { return null; }
    // Whether two paths point to the same existing file (including links and spelling differences on case-insensitive file systems)
    public static bool SameFile(string a, string b) { return false; }
    // A stamp to compare for changes (modification time and size). "" for a missing file
    public static string Stamp(string path) { return ""; }
    // Writes to a temporary file and then replaces the file. Returns the reason on failure ("" on success)
    public static string WriteText(string path, string text) { return ""; }
    // The same for bytes (one character per byte, 0 to 255; .tcsb and so on)
    public static string WriteBytes(string path, string bytes) { return ""; }
}
