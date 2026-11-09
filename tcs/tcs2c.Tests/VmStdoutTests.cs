namespace TinyCs.Tcs2c.Tests;

/// <summary>tcs VM の stdout は、file や pipe に向けても行ごとに出る (Lua の print と同じ)。終わらない program でも行が届く。</summary>
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
