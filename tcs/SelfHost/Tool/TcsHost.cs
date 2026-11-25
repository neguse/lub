// Declarations of the OS functions used by the tcs host (SelfHost/Tool). Each host has its own implementation:
// the C build uses SelfHost/Native/host.c (--ref of tcs2c), and .NET uses Transpiler/DotnetHost.cs.
// This file is passed as --ref in the C build and is not included in the .NET build.
public static class TcsHost
{
    // Command-line arguments (index 0 is the first argument; the executable name is not included)
    public static int ArgCount() { return 0; }
    public static string Arg(int index) { return ""; }
    public static bool FileExists(string path) { return false; }
    // Whether two paths point to the same existing file (including links and spelling differences on case-insensitive file systems)
    public static bool SameFile(string a, string b) { return false; }
    // File contents (UTF-8). "" if unreadable
    public static string ReadFile(string path) { return ""; }
    // Writes to a temporary file and then replaces the file. Returns the reason on failure ("" on success).
    public static string WriteFileAtomic(string path, string text) { return ""; }
    // Writes bytes (one character per byte, 0 to 255; .tcsb and so on) to a temporary file and then replaces the file. Returns the reason on failure.
    public static string WriteBinaryAtomic(string path, string bytes) { return ""; }
    public static void WriteOut(string text) { }
    public static void WriteErr(string text) { }
    // Flushes standard output (messages to the watch host arrive in the order they were written)
    public static void FlushOut() { }
    // A stamp to compare for changes (modification time and size). "" for a missing file
    public static string FileStamp(string path) { return ""; }
    // Files shipped with tcs (`lib/TinySystem/Linq.cs`, `runtime/core/tcs_core.c`, etc.). "" if missing
    public static string ToolFile(string name) { return ""; }
    public static void Sleep(int milliseconds) { }
    public static void SetExitCode(int code) { }
    // The work is done (the host stops calling Step)
    public static void SetDone() { }
}
