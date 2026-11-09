using System.Globalization;

namespace TinyCs.Tcs2c.Tests;

// float の既定の文字列化 (WriteLine・連結・補間・ToString・float? の連結・string.Join) が .NET の float.ToString() (InvariantCulture) と同じ文字列になる (il-spec §13)
public partial class DifferentialTests
{
    // 値と、CoreCLR (InvariantCulture) の float.ToString() の結果
    private static readonly (float Value, string Text)[] FloatTexts =
    [
        (0f, "0"), (-0f, "-0"), (1f, "1"), (-1f, "-1"), (0.1f, "0.1"), (0.3f, "0.3"),
        (0.33333334f, "0.33333334"), (0.6666667f, "0.6666667"), (3.1415927f, "3.1415927"),
        (1.5f, "1.5"), (100f, "100"), (100000f, "100000"), (1000000f, "1000000"),
        (1000000.1f, "1000000.1"), (1234567f, "1234567"), (1234567.5f, "1234567.5"),
        (8388608f, "8388608"), (9999999f, "9999999"), (10000000f, "10000000"),
        (10000001f, "10000001"), (12345678f, "12345678"), (16777215f, "16777215"),
        (16777216f, "16777216"), (99999990f, "99999990"), (100000000f, "100000000"),
        (123456790f, "123456790"), (1E+09f, "1E+09"), (1.234568E+09f, "1.234568E+09"),
        (-1E+09f, "-1E+09"), (1E+10f, "1E+10"), (1.5E+15f, "1.5E+15"), (1E+20f, "1E+20"),
        (-1.5474251E+26f, "-1.5474251E+26"), (-1.2379401E+27f, "-1.2379401E+27"),
        (3.4028235E+38f, "3.4028235E+38"), (0.001f, "0.001"), (-0.001f, "-0.001"),
        (0.0001f, "0.0001"), (0.00012345f, "0.00012345"), (9.9999E-05f, "9.9999E-05"),
        (1E-05f, "1E-05"), (1.2345E-05f, "1.2345E-05"), (1.2621775E-29f, "1.2621775E-29"),
        (1.1754944E-38f, "1.1754944E-38"), (1.1754942E-38f, "1.1754942E-38"),
        (1E-40f, "1E-40"), (3E-45f, "3E-45"), (1E-45f, "1E-45"), (-1E-45f, "-1E-45"),
        (float.NaN, "NaN"), (float.PositiveInfinity, "Infinity"),
        (float.NegativeInfinity, "-Infinity"),
    ];

    [CFact]
    public void FloatToString_BoundaryValuesMatchDotnetOnEveryPath()
    {
        foreach (var (value, text) in FloatTexts)
            Assert.Equal(text, value.ToString(CultureInfo.InvariantCulture));

        var calls = string.Concat(FloatTexts.Select(c => $"        Show({FloatLiteral(c.Value)});\n"));
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static float p = 0f;

                static void Show(float f)
                {
                    float? n = f;
                    Console.WriteLine(f);
                    Console.WriteLine("" + f + " " + $"{f}" + " " + f.ToString() + " " + n);
                }

                public static void Main()
                {
            """ + calls + "    }\n}\n", "P");
        Assert.Equal(string.Join("\n", FloatTexts.Select(c => $"{c.Text}\n{c.Text} {c.Text} {c.Text} {c.Text}")),
            output);
    }

    // string.Join の要素も連結と同じ文字列化
    [CFact]
    public void FloatToString_JoinElements()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                public static void Main()
                {
                    var fs = new List<float> { 1000000f, 2f / 3f, 1.5f };
                    Console.WriteLine(string.Join(",", fs));
                    Console.WriteLine(string.Join(", ", new float[] { 0.1f, 1E+10f, 1E-05f }));
                    Console.WriteLine(string.Join(",", fs.Select(f => f * 2)));
                    Console.WriteLine(string.Join("", new List<int> { 1, -2 }) + "|" + string.Join(",", new int[] { 3 }));
                    Console.WriteLine("[" + string.Join(",", new List<float>()) + "]");
                }
            }
            """, "P");
        Assert.Equal("1000000,0.6666667,1.5\n0.1, 1E+10, 1E-05\n2000000,1.3333334,3\n1-2|3\n[]", output);
    }

    // 広い指数の範囲 (非正規化数を含む) の値を文字列にした hash を、テストのプロセスの .NET と比べる
    [CFact]
    public void FloatToString_WideRangeHashMatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using TinySystem.Text;
            using System;
            public class P
            {
                static int state = 12345;

                static int Step()
                {
                    state = state * 1664525 + 1013904223;
                    return (state >> 8) & 0xffffff;
                }

                // [1, 2) の仮数に 2^e (e は -149..127) を掛けて符号を付ける
                static float Next()
                {
                    float x = 1f + (Step() & 0x7fffff) / 8388608f;
                    int e = Step() % 277 - 149;
                    while (e > 0) { x *= 2f; e--; }
                    while (e < 0) { x *= 0.5f; e++; }
                    return (Step() & 1) == 0 ? x : -x;
                }

                public static void Main()
                {
                    int h = 0;
                    for (int i = 0; i < 20000; i++)
                    {
                        string s = "" + Next();
                        var bytes = new Utf8Bytes(s); for (int j = 0; j < bytes.Length; j++) h = h * 31 + bytes[j];
                    }
                    Console.WriteLine(h);
                }
            }
            """, "P");
        Assert.Equal(DotnetWideRangeHash().ToString(CultureInfo.InvariantCulture), output);
    }

    private static string FloatLiteral(float v) =>
        float.IsNaN(v) ? "p / p"
        : float.IsPositiveInfinity(v) ? "1f / p"
        : float.IsNegativeInfinity(v) ? "-1f / p"
        : v == 0f && float.IsNegative(v) ? "-1f * p"
        : F(v);

    private static int DotnetWideRangeHash()
    {
        int state = 12345, h = 0;
        int Step()
        {
            state = unchecked(state * 1664525 + 1013904223);
            return (state >> 8) & 0xffffff;
        }
        for (int i = 0; i < 20000; i++)
        {
            float x = 1f + (Step() & 0x7fffff) / 8388608f;
            int e = Step() % 277 - 149;
            while (e > 0) { x *= 2f; e--; }
            while (e < 0) { x *= 0.5f; e++; }
            var s = ((Step() & 1) == 0 ? x : -x).ToString(CultureInfo.InvariantCulture);
            foreach (var c in s) h = unchecked(h * 31 + c);
        }
        return h;
    }
}
