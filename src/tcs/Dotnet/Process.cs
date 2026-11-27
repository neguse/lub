using System;
using System.Threading;

// .NET implementation of what a command line program written in TinyC# asks of its process (the declarations are in Cli/CliProcess.cs; C: Native/main.c)
public static class CliProcess
{
    public static void Out(string text) => Console.Out.Write(text);
    public static void Err(string text) => Console.Error.Write(text);
    public static void Flush() => Console.Out.Flush();
    public static void Sleep(int milliseconds) => Thread.Sleep(milliseconds);
}
