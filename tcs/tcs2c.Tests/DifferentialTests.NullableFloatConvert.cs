namespace TinyCs.Tcs2c.Tests;

// int? → float? の暗黙変換 (比較の昇格、代入、引数) も整数を f32 に丸める。値なしは値なしのまま。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void NullableIntToNullableFloat_RoundsToFloat32()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static bool Same(float? x, float? y) => x == y;
                public static void Main()
                {
                    int? a = 16777217;
                    float? b = 16777216f;
                    Console.WriteLine(a <= b ? 1 : 0);
                    Console.WriteLine(b >= a ? 1 : 0);
                    Console.WriteLine(a == b);
                    Console.WriteLine(a != b);
                    float? c = a;
                    Console.WriteLine(c == b);
                    Console.WriteLine(Same(a, b));
                    float? d = (float?)a;
                    Console.WriteLine(d == b);
                    int? none = null;
                    float? e = none;
                    Console.WriteLine(e.HasValue);
                    Console.WriteLine(none <= b);
                    Console.WriteLine(none == b);
                    Console.WriteLine(((a) <= (b)) ? 1 : 0);
                    int? small = 3;
                    float? half = small;
                    Console.WriteLine(half.Value / 2);
                    Console.WriteLine((a + b).Value);
                }
            }
            """, "P");
        Assert.Equal("1\n1\nTrue\nFalse\nTrue\nTrue\nTrue\nFalse\nFalse\nFalse\n1\n1.5\n33554432", output);
    }
}
