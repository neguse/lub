using System;

namespace Tcs;

// The CLI itself is Cli/Cli.cs (TinyC#). The .NET build only runs it: the arguments, then steps until the exit code
public class Program
{
    // The CLI and its state are static, so concurrent calls in the same process run one at a time (tests call Main in parallel)
    private static readonly object Gate = new();

    public static int Main(string[] args) => Run(args, Cli.Arg, Cli.Step);

    // Runs a command line program written in TinyC# (tcs, lub-gen): gives each argument to arg, then calls step until it returns the exit code
    public static int Run(string[] args, Action<string> arg, Func<int> step)
    {
        lock (Gate)
        {
            foreach (var a in args) arg(a);
            int code;
            do code = step();
            while (code < 0);
            Console.Out.Flush();
            return code;
        }
    }
}
