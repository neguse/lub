using System.Text;

namespace TinyCs.Tcs2c.Tests;

// float.Parse / float.TryParse は .NET の既定 (NumberStyles.Float | AllowThousands、InvariantCulture) と同じ文字列を受け、最も近い float (ちょうど中間は偶数) に読む。
// int.Parse / int.TryParse は NumberStyles.Integer。
// 前後の空白は ASCII の 6 文字で、末尾の NUL は読み飛ばし、Infinity / NaN の綴りは大文字小文字を問わず前後の Unicode の空白も除く (il-spec §13)
public partial class DifferentialTests
{
    // 1 と次の float の中間 (1 + 2^-24) の正確な 10 進
    private const string HalfAfterOne = "1.000000059604644775390625";

    // 0 と最小の非正規化数の中間 (2^-150) の正確な 10 進
    private const string HalfMinSubnormal =
        "0.000000000000000000000000000000000000000000000700649232162408535461864791644958065640130970938257885878534141944895541342930300743319094181060791015625";

    // 最小の非正規化数と次の値の中間 (3 * 2^-150)
    private const string HalfAfterMinSubnormal =
        "0.000000000000000000000000000000000000000000002101947696487225606385594374934874196920392912814773657635602425834686624028790902229957282543182373046875";

    // 最大の非正規化数と最小の正規化数の中間 (2^-126 - 2^-150)
    private const string HalfBelowMinNormal =
        "1.1754942807573642917278829910357665133228589927589904276829631184250030649651730385585324256680905818939208984375E-38";

    // float.MaxValue と 2^128 の中間 (2^128 - 2^103)
    private const string HalfAfterMax = "340282356779733661637539395458142568448";

    private static readonly string[] ParseInputs =
    [
        // 空白
        "", " ", "  ", "\t", "\n", "\r", "\v", "\f", " \t\r\n\v\f ", " 1.25 ", "\t1.25\n", "\v1\f", "\r-2\r",
        "1 2", "1. 5", " . ", "\u00A012", "12\u00A0", "1\u00A0000", "\u30001", "\u20031\u2003",
        // 符号
        "+1", "-1", "+-1", "-+1", "--1", "++1", "- 1", "+ 1", "1-", "1+", "-", "+", "-.", "+.", ".",
        "-0", "+0", "-0.0", "-0e5", "0", "00", "000.000", "-000",
        // 小数点
        ".5", "5.", "-.5", "+.5", "1..2", "1.2.3", "..5", "1.5.",
        // 指数
        "1e5", "1E5", "1e+5", "1e-5", "1E-5", "1e05", "1.e5", ".5e1", "1e", "1e+", "1e-", "e5", ".e5",
        "1e 5", "1e+-5", "1e5.5", "1e5e5", "1ee5", "1e0", "1e-0", "0e0", "1e1000000000", "1e-1000000000",
        "1e2147483648", "-1e2147483648", "0e99999999999", "1e99999999999999999999", "0.0000001e100000000000",
        "1e0000000000000000000000005", "-1e-99999999999",
        // 桁区切り (小数点より前の数字の後だけ)
        "1,000", "-1,000", "1,234,567", "1,234.5", "1,234e2", ",1000", "1.2,3", "1e1,0", "1_000", "1,",
        "1,,000", "1,.5", "1,e5", "0,1", "-,1", "-0,0", ",", "-,", "1 ,000", "1, 000", "1,000 ", " 1,000",
        "12,34,5", "1,2,3.4,5", "1'000", "1.000,5",
        // .NET が受けない C などの書き方
        "0x1p2", "0x10", "0X1P2", "-0x1.8p2", "0x1.8", " 0x1p2 ", "0x", "0x1p", "0x1p2x", "1f", "1.5f", "1d",
        "1m", "1L", "0b1", "1e5f", "inf", "-inf", "+inf", "nan(payload)", "infinite", "\u221E", "-\u221E",
        // Infinity と NaN
        "Infinity", "infinity", "INFINITY", "InFiNiTy", "-Infinity", "+Infinity", "-infinity", "+INFINITY",
        "+-Infinity", "-+Infinity", "- Infinity", "+ Infinity", " Infinity ", "\tInfinity\n", "Infinityx",
        "Infinit", "Infinity1", "1Infinity", "NaN", "nan", "NAN", "-NaN", "+NaN", "-nan", "+-NaN", "--NaN",
        " NaN ", "NaN1", "NaNx", "\u00A0Infinity\u00A0", "\u3000NaN\u2003", "\u0085NaN", "\u200BNaN",
        "\u2028-Infinity\u2029", "\u00A0+NaN", "-\u00A0NaN", "\u0131nfinity", "\uFF11", "\u0661",
        // NUL (末尾の NUL だけ読み飛ばす)
        "\0", "1\0", "1 \0", "1\0 ", "1\0\0", "\01", "1\0x", "1\0 2", "1\02", " 1 \0\0", "NaN\0",
        "Infinity\0", "\0NaN", "1,000\0", "1e5\0", "1e\0", "-\0", "1.5\0\0\0",
        // 丸め
        "1.0000000596046448", HalfAfterOne, "1.0000000596046447", "1.000000059604644775390626",
        "1.000000178813934326171875", "0.1", "0.2", "0.3", "3.14159265358979323846", "16777216", "16777217",
        "16777218", "16777219", "33554431", "2147483647", "-2147483648", "2147483648", "-2147483649",
        "4294967295", "123456789", "0.000001", "1e-10", "9.999999e-39", "00000000000000000000000012",
        "99999999999",
        // 最大の値と ∞ の境
        "3.4028235e38", "3.40282347e38", "3.4028235677973366e38", "3.4028235677973367e38", HalfAfterMax,
        "340282356779733661637539395458142568447", "-" + HalfAfterMax, "3.4028236e38", "1e38", "1e39", "-1e39",
        // 正規化数と非正規化数と 0 の境
        "1.17549435e-38", "1.1754942e-38", "1.1754943508222875e-38", HalfBelowMinNormal, "1.4e-45", "1e-45",
        "1.401298464324817e-45", "7e-46", "7.006e-46", "7.0064923216240862e-46", HalfMinSubnormal,
        "-" + HalfMinSubnormal, HalfMinSubnormal + "1", HalfAfterMinSubnormal, HalfAfterMinSubnormal + "1",
        "0.000000000000000000000000000000000000000000002101947696487225606385594374934874196920392912814773657635602425834686624028790902229957282543182373046874",
        "1e-46", "-1e-46", "1e-50", "-1e-50",
        // 長い数字の列
        new string('0', 126) + "1", new string('0', 127) + "1", new string('0', 200) + "1",
        new string('0', 2000) + "1", "0." + new string('0', 2000) + "1e2010", "1" + new string('0', 2000) + "e-2000",
        "1" + new string('0', 38), "1" + new string('0', 39), "0." + new string('0', 44) + "1",
        "0." + new string('0', 45) + "1", HalfAfterOne + new string('0', 1000), HalfAfterOne + new string('0', 1000) + "1",
        "1.000000059604644775390624" + new string('9', 1000), HalfMinSubnormal + new string('0', 1000) + "1",
        new string('9', 1000), "0." + new string('9', 1000), " " + new string('1', 300) + " ",
        "1." + new string('1', 3000), "1" + string.Concat(Enumerable.Repeat(",000", 12)),
        new string('1', 100) + "e-90",
    ];

    // TinyC# の string literal (ASCII の印字できる文字の外は \u の escape)
    private static string StringLiteral(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c == '"' || c == '\\') sb.Append('\\').Append(c);
            else if (c >= 0x20 && c <= 0x7E) sb.Append(c);
            else sb.Append("\\u").Append(((int)c).ToString("X4"));
        }
        return sb.Append('"').ToString();
    }

    private static string ParseProgram(string body) => """
        using System;
        public class P
        {
            static void T(int n, string s)
            {
                float f = 123f;
                bool ok = float.TryParse(s, out f);
                int i = 123;
                bool iok = int.TryParse(s, out i);
                Console.WriteLine(n + " " + (ok ? "1:" : "0:") + f + " " + (iok ? "1:" : "0:") + i);
            }

            public static void Main()
            {
        """ + body + """
            }
        }
        """;

    [CFact]
    public void FloatTryParse_ReportedCases_MatchDotnet()
    {
        var output = Backends.AssertParity(ParseProgram("""
                    T(0, " ");
                    T(1, " \t\r\n");
                    T(2, "0x1p2");
                    T(3, "-0x1.8p2");
                    T(4, "1.0000000596046448");
                    T(5, "1,000");
                    T(6, "1,234.5");
                    T(7, "1\0x");
                    T(8, "1\0 ");
                    string text = "1";
                    for (int k = 0; k < 200; k++) text = "0" + text;
                    T(9, text);
                    float g = 0f;
                    float.TryParse("1.0000000596046448", out g);
                    Console.WriteLine(g > 1f);
                    float n = 0f;
                    Console.WriteLine(float.TryParse(1234.5f.ToString("N1"), out n) + " " + n);
            """), "P");
        Assert.Equal("""
            0 0:0 0:0
            1 0:0 0:0
            2 0:0 0:0
            3 0:0 0:0
            4 1:1.0000001 0:0
            5 1:1000 0:0
            6 1:1234.5 0:0
            7 0:0 0:0
            8 0:0 0:0
            9 1:1 1:1
            True
            True 1234.5
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void FloatAndIntTryParse_InputTable_MatchDotnet()
    {
        var body = string.Concat(ParseInputs.Select((s, n) => $"        T({n}, {StringLiteral(s)});\n"));
        var output = Backends.AssertParity(ParseProgram(body), "P");
        Assert.Equal(ParseInputs.Length, output.Split('\n').Length);
    }

    [CFact]
    public void FloatAndIntParse_AcceptedText_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(float.Parse("1,000"));
                    Console.WriteLine(float.Parse("1.0000000596046448"));
                    Console.WriteLine(float.Parse(" -1,234.5e-2 \0"));
                    Console.WriteLine(float.Parse("+infinity"));
                    Console.WriteLine(float.Parse("\u3000-nan\u2003"));
                    Console.WriteLine(float.Parse("1e39"));
                    Console.WriteLine(int.Parse(" 12 \0"));
                    Console.WriteLine(int.Parse("-0"));
                }
            }
            """, "P");
        Assert.Equal("1000\n1.0000001\n-12.345\nInfinity\nNaN\nInfinity\n12\n0", output);
    }

    [CTheory]
    [InlineData("float", "\"\"")]
    [InlineData("float", "\" \"")]
    [InlineData("float", "\" \\t\\n\"")]
    [InlineData("float", "\"0x1p2\"")]
    [InlineData("float", "\"1\\0x\"")]
    [InlineData("float", "\"1_000\"")]
    [InlineData("float", "\",1\"")]
    [InlineData("float", "\"inf\"")]
    [InlineData("float", "\"NaN\\0\"")]
    [InlineData("float", "\"1e\"")]
    [InlineData("int", "\"1\\0x\"")]
    [InlineData("int", "\"1,000\"")]
    [InlineData("int", "\"2147483648\"")]
    public void FloatAndIntParse_InvalidText_Faults(string type, string literal)
    {
        var output = Backends.AssertFaultParity($$"""
            using System;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine("before");
                    Console.WriteLine({{type}}.Parse({{literal}}));
                    Console.WriteLine("after");
                }
            }
            """, "P");
        Assert.Equal("before", output);
    }
}
