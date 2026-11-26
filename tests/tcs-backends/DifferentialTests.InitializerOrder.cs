namespace TinyCs.Tcs2c.Tests;

// Two-backend differential tests for the execution order of field and auto-property initializers. The expected values are the
// stdout of running the same program on CoreCLR
public class InitializerOrderDifferentialTests
{
    // C# §15.5.6: initializers run in declaration order, whether they belong to a field or an auto-property
    // (instance ones at the start of the constructor, static ones at type initialization)
    [CFact]
    public void FieldAndAutoPropertyInitializers_RunInDeclarationOrder()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            public class Log { public static string Text = ""; public static int Hit(string s) { Text += s; return 0; } }
            public class W
            {
                public int A = Log.Hit("A");
                public int Value { get; set; } = Log.Hit("P");
                public int B = Log.Hit("B");
                public string Name { get; set; } = Log.Hit("Q") + "n";
            }
            public class S
            {
                public static string Order = "";
                public static int X = Add("x");
                public static int Y { get; set; } = Add("y");
                public static int Z = Add("z");
                static int Add(string s) { Order += s; return Order.RuneCount(); }
            }
            public class P
            {
                public static void Main()
                {
                    W w = new W();
                    Console.WriteLine(Log.Text + " " + w.Name);
                    Console.WriteLine(S.Order + " " + S.X + S.Y + S.Z);
                }
            }
            """, "P");
        Assert.Equal("APBQ 0n\nxyz 123", output);
    }
}
