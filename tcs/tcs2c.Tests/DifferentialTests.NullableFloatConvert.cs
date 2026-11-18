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

    // int (と char) から float? への明示の cast も、暗黙の変換と同じく f32 に丸めてから包む。
    // 結果は float? として引数、List の要素、補間にそのまま使える
    [CFact]
    public void IntToNullableFloatCast_RoundsToFloat32()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class P
            {
                static float? Id(float? v) => v;
                public static void Main()
                {
                    int x = 16777217;
                    float? f = (float?)x;
                    Console.WriteLine(f);
                    Console.WriteLine((int)f.Value);
                    Console.WriteLine((float?)(-x));
                    Console.WriteLine((float?)16777217);
                    Console.WriteLine((float?)x == 16777216f);
                    Console.WriteLine(Id((float?)x));
                    var list = new List<float?> { (float?)x };
                    Console.WriteLine(list[0]);
                    Console.WriteLine($"{(float?)x}");
                    float? g = null;
                    g = (float?)x;
                    Console.WriteLine(g == f);
                    char c = 'A';
                    Console.WriteLine((float?)c);
                    Console.WriteLine((int?)c);
                    // 対照: 暗黙の変換と、int? を経る cast
                    float? implicitly = x;
                    Console.WriteLine(implicitly);
                    Console.WriteLine((float?)(int?)x);
                }
            }
            """, "P");
        Assert.Equal("16777216\n16777216\n-16777216\n16777216\nTrue\n16777216\n16777216\n16777216\nTrue\n65\n65\n16777216\n16777216", output);
    }
}
