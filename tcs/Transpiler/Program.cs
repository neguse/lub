namespace TinyCs;

// CLI の本体は SelfHost/Tool/Cli.cs (TinyC#)。.NET の build はその上の host (DotnetHost.cs) を起動するだけ
public class Program
{
    public static int Main(string[] args) => TcsHost.Run(args);
}
