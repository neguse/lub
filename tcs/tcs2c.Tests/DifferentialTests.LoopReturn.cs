namespace TinyCs.Tcs2c.Tests;

// Lua の return は block の最後の文に限られる。loop 本体の末尾の return の後に
// continue label や for の増分が続く形、到達不能文が続く形を C# どおりに動かす
public partial class DifferentialTests
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
