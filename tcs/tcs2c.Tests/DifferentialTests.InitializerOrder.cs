namespace TinyCs.Tcs2c.Tests;

// field と auto-property の初期化子の実行順の 2 backend differential。期待値は
// 同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    // C# §15.5.6: 初期化子は field と auto-property の区別なく宣言順に実行する
    // (instance は constructor の冒頭、static は型の初期化)
    [CFact]
    public void FieldAndAutoPropertyInitializers_RunInDeclarationOrder()
    {
        var output = Backends.AssertParity("""
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
                static int Add(string s) { Order += s; return Order.Length; }
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
