using System.Globalization;

namespace TinyCs.Tcs2c.Tests;

// 補間の数値の標準書式 (int の G / E / F / N / D / X / R、float の F / N / R と大きい精度) が .NET の ToString(format) (InvariantCulture) と同じ文字列になる (il-spec §13)
public partial class DifferentialTests
{
    private static string Holes(string name, string[] formats) =>
        string.Join("|", formats.Select(f => "{" + name + ":" + f + "}"));

    private static string Texts<T>(T value, string[] formats) where T : IFormattable =>
        string.Join("|", formats.Select(f => value.ToString(f, CultureInfo.InvariantCulture)));

    private static readonly string[] IntFormats =
        ["G", "g", "G0", "G1", "G2", "G3", "G7", "G10", "G12", "g3", "G03",
         "E", "e", "E0", "E1", "E2", "e2", "E10", "E20",
         "F", "f", "F0", "F2", "f1", "F20", "N", "n", "N0", "N1", "N20",
         "D", "d", "D0", "D3", "D12", "D03", "D100", "X", "x", "X0", "X8", "x4", "X12", "X100", "F100", "R", "r", "R2"];

    private static readonly int[] IntFormatValues =
        [0, 1, -1, 5, -7, 15, 25, 45, 95, 1000, 1999, 999999, 1234567, -1234567,
         1250000, -1250000, 1350000, 12345678, 2147483647, int.MinValue];

    [CFact]
    public void IntFormat_MatchesDotnet()
    {
        var calls = string.Concat(IntFormatValues.Select(v =>
            $"        Show({(v == int.MinValue ? "int.MinValue" : v.ToString(CultureInfo.InvariantCulture))});\n"));
        var output = Backends.AssertParity("""
            using System;
            public class P
            {

            """ + "    static void Show(int x) { Console.WriteLine($\"" + Holes("x", IntFormats) + "\"); }\n" + """

                public static void Main()
                {
                    int? n = 1234567;
                    int? none = null;
                    Console.WriteLine($"{n:N0}|{n:E2}|{none:N0}|{none:X}|{1234567,14:E2}|{1234,-8:N0}|");
            """ + calls + "    }\n}\n", "P");
        Assert.Equal("1,234,567|1.23E+006|||     1.23E+006|1,234   |\n"
            + string.Join("\n", IntFormatValues.Select(v => Texts(v, IntFormats))), output);
    }

    // 実行の int は 32 bit (il-spec §3)。64 bit の Lua では 2^31 以上の uint が integer のまま届くので、切り詰めて別の値を書かず、lua32 と C と同じく止める
    [CTheory]
    [InlineData("D")]
    [InlineData("X")]
    [InlineData("G")]
    [InlineData("N0")]
    [InlineData("E2")]
    public void IntFormat_OutsideInt32StopsOnEveryBackend(string format)
    {
        var program = $$"""
            using System;
            public static class P
            {
                public static void Main()
                {
                    uint u = 3000000000;
                    Console.WriteLine($"{u:{{format}}}");
                }
            }
            """;
        Assert.Contains("no integer representation",
            Assert.Throws<InvalidOperationException>(() => Backends.RunLua([program], "P")).Message);
        Assert.Contains("number is out of the int range",
            Assert.Throws<InvalidOperationException>(() => Backends.RunLua([program], "P", luaPath: Backends.Lua64Path)).Message);
        Assert.Contains("invalid i32 literal",
            Assert.Throws<TinyCs.Tcs2c.Tcs2cException>(() => Backends.EmitC([program], "P")).Message);
    }

    [CFact]
    public void IntFormat_Int32BoundsPassOn64BitLua()
    {
        var output = Backends.RunLua(["""
            using System;
            public static class P
            {
                public static void Main()
                {
                    uint u = 2147483647;
                    int m = int.MinValue;
                    Console.WriteLine($"{u:D}|{u:X}|{u:N0}|{m:D}|{m:X}|{m:N0}");
                }
            }
            """], "P", luaPath: Backends.Lua64Path);
        Assert.Equal("2147483647|7FFFFFFF|2,147,483,647|-2147483648|80000000|-2,147,483,648", output);
    }

    private static readonly string[] FloatFixedFormats =
        ["F", "f", "F0", "F1", "F2", "F3", "f2", "F02", "F9", "F10", "F20", "F60",
         "F100", "N", "n", "N0", "N1", "N2", "n3", "N02", "N20", "N60", "N100",
         "R", "r", "R3", "R0", "G40", "E40"];

    [CFact]
    public void FloatFormat_FixedAndRoundTripMatchDotnet()
    {
        float[] values =
        [
            .. FloatFormatTexts.Select(c => c.Value),
            0.5f, 1.5f, 0.05f, 0.06f, 0.95f, 1.25f, 1.75f, 1234.5f, -1234567.9f, 999.995f, 999999.9f, -0.001f, 0.004f, 0.006f, 1.2345678f,
        ];
        var calls = string.Concat(values.Select(v => $"        Show({FloatLiteral(v)});\n"));
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static float p = 0f;

            """ + "    static void Show(float f) { Console.WriteLine($\"" + Holes("f", FloatFixedFormats) + "\"); }\n" + """

                public static void Main()
                {
                    float? n = 1234.5f;
                    float? none = null;
                    Console.WriteLine($"{n:N}|{n:F1}|{n:R}|{none:N}|{none:F}|{none:R}|{1234.5f,12:N}|{0.5f,-8:F}|");
            """ + calls + "    }\n}\n", "P");
        Assert.Equal("1,234.50|1234.5|1234.5||||    1,234.50|0.50    |\n"
            + string.Join("\n", values.Select(v => Texts(v, FloatFixedFormats))), output);
    }

    private const string FormatHashFloats = "F|F0|F1|F2|F9|F50|N|N0|N3|R|r";
    private const string FormatHashInts = "G|G1|G3|G5|G9|E|E0|E3|E9|e5|F|F3|N|N0|N2|D|D5|D12|X|x|X10|R";

    // 広い範囲の値を書式つきで文字列にした hash を、テストのプロセスの .NET と比べる
    [CFact]
    public void NumberFormat_WideRangeHashMatchesDotnet()
    {
        var floatHoles = Holes("x", FormatHashFloats.Split('|'));
        var intHoles = Holes("v", FormatHashInts.Split('|'));
        var output = Backends.AssertParity("""
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

                // 桁数がばらけるように上の bit を右シフトで落とす
                static int NextInt()
                {
                    int v = (Step() << 8) ^ Step();
                    return v >> (Step() % 32);
                }

                public static void Main()
                {
                    int h = 0;
                    for (int i = 0; i < 5000; i++)
                    {
                        float x = Next();
                        int v = NextInt();

            """ + "            string s = $\"" + floatHoles + "|" + intHoles + "\";\n" + """
                        for (int j = 0; j < s.Length; j++) h = h * 31 + s[j];
                    }
                    Console.WriteLine(h);
                }
            }
            """, "P");
        Assert.Equal(DotnetNumberFormatHash().ToString(CultureInfo.InvariantCulture), output);
    }

    private static int DotnetNumberFormatHash()
    {
        var floats = FormatHashFloats.Split('|');
        var ints = FormatHashInts.Split('|');
        int state = 12345, h = 0;
        int Step()
        {
            state = unchecked(state * 1664525 + 1013904223);
            return (state >> 8) & 0xffffff;
        }
        for (int i = 0; i < 5000; i++)
        {
            float x = 1f + (Step() & 0x7fffff) / 8388608f;
            int e = Step() % 277 - 149;
            while (e > 0) { x *= 2f; e--; }
            while (e < 0) { x *= 0.5f; e++; }
            x = (Step() & 1) == 0 ? x : -x;
            int v = unchecked(Step() << 8) ^ Step();
            v >>= Step() % 32;
            var s = Texts(x, floats) + "|" + Texts(v, ints);
            foreach (var c in s) h = unchecked(h * 31 + c);
        }
        return h;
    }
}
