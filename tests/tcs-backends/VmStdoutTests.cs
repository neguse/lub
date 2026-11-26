namespace TinyCs.Tcs2c.Tests;

/// <summary>tcs VM stdout is written line by line even when directed to a file or pipe (same as Lua's print). Lines arrive even from programs that do not terminate.</summary>
public class VmStdoutTests
{
    [CFact]
    public void ConsoleOutput_ReachesAPipePerLine()
    {
        var lines = Backends.FirstVmLines("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine("hello");
                    Console.Write("a");
                    Console.Write("b\n");
                    int n = 0;
                    while (true) n++;
                }
            }
            """, "P", 2, TimeSpan.FromSeconds(10));
        Assert.Equal(["hello", "ab"], lines);
    }
}
