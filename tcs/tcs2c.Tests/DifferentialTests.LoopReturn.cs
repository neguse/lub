namespace TinyCs.Tcs2c.Tests;

// Lua's return is limited to the last statement of a block. Forms where the return at the end of a loop body is followed by
// a continue label or a for increment, and forms followed by unreachable statements, run as in C#
public class LoopReturnDifferentialTests
{
    [CFact]
    public void Loop_TrailingReturnFollowedByLabelOrStep_MatchesCSharp()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                static int WhileContinue(int n)
                {
                    while (true) { n++; if (n < 3) continue; return n; }
                }
                static int ForContinue(int n)
                {
                    for (int i = 0; i < 10; i++) { if (i < 2) continue; return i + n; }
                    return -1;
                }
                static int ForStep(int n)
                {
                    for (int i = n; i < 10; i += 2) { int k = i * 10; return k; }
                    return -1;
                }
                static int DoContinue(int n)
                {
                    do { n++; if (n < 4) continue; return n; } while (true);
                }
                static int ForeachContinue(List<int> l)
                {
                    foreach (var x in l) { if (x < 2) continue; return x; }
                    return -1;
                }
                static int Unreachable(int n)
                {
                    return n;
                    n++;
                }
                public static void Main()
                {
                    Console.WriteLine(WhileContinue(0) + " " + ForContinue(5) + " " + ForStep(3) + " " + DoContinue(0) + " " + ForeachContinue(new List<int> { 1, 2, 3 }) + " " + Unreachable(7));
                }
            }
            """, "P");
        Assert.Equal("3 7 30 4 2 7", output);
    }
}
