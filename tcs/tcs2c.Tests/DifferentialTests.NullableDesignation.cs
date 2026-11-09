namespace TinyCs.Tcs2c.Tests;

// T? の入力を T の宣言の pattern で照合する形 (#445)。C は値を取り出さずに `cannot assign i32? to i32` で止まっていた
public partial class DifferentialTests
{
    [CFact]
    public void NullableInput_DeclarationPattern_UnwrapsValue()
    {
        var output = Backends.AssertParity("""
            public enum Color { Red, Green }
            public static class Game
            {
                static int? Maybe(int n) => n > 0 ? n : null;
                static string Sw(float? f) => f switch { float x when x > 1f => "big " + x, float x => "small " + x, null => "none" };
                static string St(bool? b)
                {
                    switch (b)
                    {
                        case bool v: return "b " + v;
                        default: return "none";
                    }
                }
                public static void Main()
                {
                    int? m = 4;
                    if (m is int y) System.Console.WriteLine(y);
                    int? none = null;
                    System.Console.WriteLine((none is int z) ? "has " + z : "no");
                    System.Console.WriteLine((Maybe(3) is int w) ? "has " + w : "no");
                    Color? c = Color.Green;
                    if (c is Color k) System.Console.WriteLine(k);
                    System.Console.WriteLine(Sw(2.5f) + "," + Sw(0.5f) + "," + Sw(null));
                    System.Console.WriteLine(St(true) + "," + St(null));
                }
            }
            """, "Game");
        Assert.Equal("4\nno\nhas 3\nGreen\nbig 2.5,small 0.5,none\nb True,none", output);
    }
}
