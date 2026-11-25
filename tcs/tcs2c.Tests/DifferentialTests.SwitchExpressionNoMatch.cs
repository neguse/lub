namespace TinyCs.Tcs2c.Tests;

// A non-exhaustive switch expression with no matching arm faults (il-spec §12; C#'s SwitchExpressionException).
// The IL shape of a switch expression depends on where it is placed (return, a local initializer, an assignment, an if condition, inside an expression), so each position is checked.
// The expected value is the stdout of the same program run on CoreCLR (it stops with an exception after the output that precedes the non-matching input)
public class SwitchExpressionNoMatchDifferentialTests
{
    [CTheory]
    [InlineData("Console.WriteLine(Ret(1)); Console.WriteLine(Ret(x));", "before\n10")]
    [InlineData("int y = x switch { 1 => 10, 2 => 20 }; Console.WriteLine(y);", "before")]
    [InlineData("int y = 5; y = x switch { 1 => 10, 2 => 20 }; Console.WriteLine(y);", "before")]
    [InlineData("if (x switch { 1 => true, 2 => false }) Console.WriteLine(\"t\");", "before")]
    [InlineData("Console.WriteLine(\"v\" + x switch { 1 => \"a\", 2 => \"b\" });", "before")]
    [InlineData("Func<int, int> f = v => v switch { 1 => 10 }; Console.WriteLine(f(1)); Console.WriteLine(f(x));", "before\n10")]
    [InlineData("string s = x switch { 1 => \"a\", _ when x > 5 => \"big\" }; Console.WriteLine(s);", "before")]
    [InlineData("Pt p = x switch { 1 => new Pt(1, 2) }; Console.WriteLine(p.X);", "before")]
    [InlineData("Console.WriteLine(Kind(new Box())); Console.WriteLine(Kind(null));", "before\nbox")]
    public void SwitchExpressionNoMatch_FaultsAfterPrecedingOutput(string body, string expected)
    {
        Assert.Equal(expected, Backends.AssertFaultParity($$"""
            using System;
            public readonly record struct Pt(int X, int Y);
            public class Box { }
            public static class P
            {
                static int Ret(int v) => v switch { 1 => 10, 2 => 20 };
                static string Kind(Box b) => b switch { Box => "box" };
                public static void Main()
                {
                    int x = 3;
                    Console.WriteLine("before");
                    {{body}}
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // The when clauses of the arms are evaluated in written order, and it stops after them when none matches
    [CFact]
    public void SwitchExpressionNoMatch_FaultsAfterEvaluatingGuards()
    {
        Assert.Equal("""
            before
            10
            guard 3 1
            guard 3 2
            """, Backends.AssertFaultParity("""
            using System;
            public static class P
            {
                static bool Guard(int v, int arm) { Console.WriteLine("guard " + v + " " + arm); return false; }
                static int Pick(int v) => v switch { 1 => 10, int n when Guard(n, 1) => 20, _ when Guard(v, 2) => 30 };
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine(Pick(1));
                    Console.WriteLine(Pick(3));
                    Console.WriteLine("after");
                }
            }
            """, "P"));
    }

    // It does not stop when an arm matches (a discard arm, arms covering every value, a matching input)
    [CFact]
    public void SwitchExpression_MatchingArmDoesNotFault()
    {
        var output = Backends.AssertParity("""
            using System;
            public enum Dir { Left, Right }
            public static class P
            {
                static int Ret(int v) => v switch { 1 => 10, 2 => 20 };
                static int Flag(bool b) => b switch { true => 1, false => 0 };
                static string Name(Dir d) => d switch { Dir.Left => "L", Dir.Right => "R" };
                static int Other(int v) => v switch { 1 => 10, _ => -1 };
                public static void Main()
                {
                    int x = 2;
                    int y = x switch { 1 => 10, 2 => 20 };
                    int z = 0;
                    z = x switch { 2 => 7 };
                    if (x switch { 2 => true }) Console.WriteLine("t");
                    Console.WriteLine(Ret(1) + "," + Ret(2) + "," + y + "," + z);
                    Console.WriteLine(Flag(true) + "," + Flag(false) + "," + Name(Dir.Left) + Name(Dir.Right) + "," + Other(5));
                }
            }
            """, "P");
        Assert.Equal("t\n10,20,20,7\n1,0,LR,-1", output);
    }
}
