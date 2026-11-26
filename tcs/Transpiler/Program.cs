namespace TinyCs;

// The CLI itself is Tool/Cli.cs (TinyC#). The .NET build only starts the host on top of it (DotnetHost.cs)
public class Program
{
    public static int Main(string[] args) => TcsHost.Run(args);
}
