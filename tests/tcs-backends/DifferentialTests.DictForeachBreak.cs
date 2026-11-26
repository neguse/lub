namespace TinyCs.Tcs2c.Tests;

// break / continue inside a Dictionary foreach exits only that loop / moves to the next iteration as in C#, even combined with nested foreach, switch, and while.
// Only values that do not depend on iteration order are printed. The expected values are the stdout of running the same program on CoreCLR
public class DictForeachBreakDifferentialTests
{
    [CFact]
    public void DictForeach_BreakAndContinue_LeaveOnlyThatLoop()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                public static void Main()
                {
                    var d = new Dictionary<int, int> { [1] = 10, [2] = 20, [3] = 30 };
                    var l = new List<int> { 1, 2, 3 };
                    var n = 0;
                    foreach (var kv in d) { n++; break; }
                    Console.WriteLine("break " + n);
                    n = 0;
                    foreach (var kv in d) { n++; if (n == 2) break; }
                    Console.WriteLine("conditional " + n);
                    var s = 0;
                    foreach (var kv in d) { if (kv.Key == 2) continue; s += kv.Value; }
                    Console.WriteLine("continue " + s);
                    n = 0;
                    foreach (var a in d) { foreach (var b in d) { n++; break; } n += 10; }
                    Console.WriteLine("nested " + n);
                    n = 0;
                    foreach (var a in d) { foreach (var b in d) { if (b.Key == a.Key) continue; n++; } }
                    Console.WriteLine("nested continue " + n);
                    n = 0;
                    foreach (var a in d) { switch (a.Key) { case 2: break; default: n++; break; } n += 10; }
                    Console.WriteLine("switch " + n);
                    n = 0;
                    foreach (var a in d) { foreach (var x in l) { n++; if (x == 2) break; } }
                    foreach (var x in l) { foreach (var a in d) { n += 10; break; } }
                    Console.WriteLine("list " + n);
                    n = 0;
                    var w = 0;
                    while (w < 3) { w++; foreach (var a in d) { n++; break; } }
                    Console.WriteLine("while " + n);
                    n = 0;
                    foreach (var kv in d) { Func<int> f = () => kv.Value; n += f(); break; }
                    Console.WriteLine("captured " + (n >= 10 ? "y" : "n") + (n % 10));
                }
            }
            """, "P");
        Assert.Equal("break 1\nconditional 2\ncontinue 40\nnested 33\nnested continue 6\n" +
            "switch 32\nlist 36\nwhile 3\ncaptured y0", output);
    }
}
