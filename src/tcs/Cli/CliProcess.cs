// Declarations of what a command line program written in TinyC# (tcs, lub-gen) asks of its process, besides files (Api/TcsFiles.cs).
// The C build takes this file as --ref and Native/main.c implements it, and .NET uses Dotnet/Process.cs.
// This file is not included in the .NET build.
public static class CliProcess
{
    public static void Out(string text) { }
    public static void Err(string text) { }
    // Flushes standard output (messages to the parent of --watch arrive in the order they were written)
    public static void Flush() { }
    public static void Sleep(int milliseconds) { }
}
