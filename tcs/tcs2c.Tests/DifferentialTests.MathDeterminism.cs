namespace TinyCs.Tcs2c.Tests;

// Math の超越関数 (runtime/math) が tcs2c (C)・Lua (tcs_math module)・.NET
// (TinySystem.Math) で bit 単位で同じ結果を返す。決まった入力列の結果の bit 列を
// 関数ごとに hash し、3 つの経路と期待値を比べる。期待値は 1 つで、OS や CPU で
// 変わらない (変わったら実装か build の設定が決定的でなくなっている)
public partial class DifferentialTests
{
    // name, 第 1 引数の範囲, 第 2 引数の範囲
    private static readonly (string Name, float Lo, float Hi, float Lo2, float Hi2)[] MathCases =
    [
        ("sin", -10f, 10f, 0f, 0f), ("sinbig", -1e9f, 1e9f, 0f, 0f),
        ("cos", -10f, 10f, 0f, 0f), ("cosbig", -1e9f, 1e9f, 0f, 0f),
        ("tan", -10f, 10f, 0f, 0f), ("tanbig", -1e9f, 1e9f, 0f, 0f),
        ("atan2", -1f, 1f, -1f, 1f), ("exp", -10f, 10f, 0f, 0f),
        ("log", 0.001f, 1000f, 0f, 0f), ("log2", 0.001f, 1000f, 0f, 0f),
        ("log10", 0.001f, 1000f, 0f, 0f), ("logb", 0.001f, 1000f, 0f, 0f),
        ("pow", 0.01f, 10f, -4f, 4f), ("pow2", -100f, 100f, 0f, 0f),
    ];

    private const string MathDeterminismProgram = """
        using System;
        using M = TinySystem.Math;
        public class P
        {
            static int state;

            // 線形合同法 (32 bit で wrap する) の上位 24 bit から [lo, hi) の値を作る
            static float Next(float lo, float hi)
            {
                state = state * 1664525 + 1013904223;
                return lo + (hi - lo) * (((state >> 8) & 0xffffff) / 16777216f);
            }

            // binary32 の bit 列 (NaN は 1 つにまとめる)
            static int Bits(float x)
            {
                if (x != x) return 0x7fc00000;
                int sign = x < 0 || (x == 0 && 1 / x < 0) ? -2147483647 - 1 : 0;
                float a = M.Abs(x);
                if (a == 0) return sign;
                if (a > 3.4028235e38f) return sign | 0x7f800000;
                int e = 150;
                while (a >= 16777216f) { a *= 0.5f; e++; }
                while (a < 8388608f && e > 1) { a *= 2f; e--; }
                if (a < 8388608f) return sign | (int)a;
                return sign | (e << 23) | ((int)a - 8388608);
            }

            static float Apply(string name, float a, float b)
            {
                switch (name)
                {
                    case "sin": case "sinbig": return M.Sin(a);
                    case "cos": case "cosbig": return M.Cos(a);
                    case "tan": case "tanbig": return M.Tan(a);
                    case "atan2": return M.Atan2(a, b);
                    case "exp": return M.Exp(a);
                    case "log": return M.Log(a);
                    case "log2": return M.Log(a, 2f);
                    case "log10": return M.Log(a, 10f);
                    case "logb": return M.Log(a, 3f);
                    case "pow": return M.Pow(a, b);
                    default: return M.Pow(a, 2f);
                }
            }

            static void Hash(string name, float lo, float hi, float lo2, float hi2)
            {
                state = 12345;
                int h = 0;
                for (int i = 0; i < 4000; i++)
                {
                    float a = Next(lo, hi);
                    float b = Next(lo2, hi2);
                    h = h * 31 + Bits(Apply(name, a, b));
                }
                Console.WriteLine(name + " = " + h);
            }

            public static void Main()
            {
        """;

    [CFact]
    public void Math_TranscendentalsAreBitIdenticalAcrossBackends()
    {
        var calls = string.Concat(MathCases.Select(c =>
            $"        Hash(\"{c.Name}\", {F(c.Lo)}, {F(c.Hi)}, {F(c.Lo2)}, {F(c.Hi2)});\n"));
        var output = Backends.AssertParity(MathDeterminismProgram + calls + "    }\n}\n", "P");
        Assert.Equal(DotnetMathHashes(), output);
        Assert.Equal("""
            sin = -1646256737
            sinbig = 1743731354
            cos = 1408592815
            cosbig = 1725535950
            tan = -1521153500
            tanbig = -1641175423
            atan2 = 645430735
            exp = 644293448
            log = 749607063
            log2 = 1420156633
            log10 = -468863173
            logb = -534007121
            pow = 1515723317
            pow2 = -833941229
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Math_TinySystemCeilingMatchesCeil()
    {
        var output = Backends.AssertParity("""
            using System;
            using M = TinySystem.Math;
            public class P
            {
                public static void Main()
                {
                    Console.WriteLine(M.Ceiling(2.25f) + " " + M.Ceiling(-2.25f) + " " + M.Ceil(2.25f));
                }
            }
            """, "P");
        Assert.Equal("3 -2 3", output);
    }

    private static string F(float v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "f";

    // 同じ program を .NET の TinySystem.Math で計算した stdout
    private static string DotnetMathHashes()
    {
        var text = new System.Text.StringBuilder();
        foreach (var (name, lo, hi, lo2, hi2) in MathCases)
        {
            int state = 12345, h = 0;
            float Next(float l, float u)
            {
                state = unchecked(state * 1664525 + 1013904223);
                return l + (u - l) * (((state >> 8) & 0xffffff) / 16777216f);
            }
            for (int i = 0; i < 4000; i++)
            {
                float a = Next(lo, hi), b = Next(lo2, hi2);
                float r = name switch
                {
                    "sin" or "sinbig" => TinySystem.Math.Sin(a),
                    "cos" or "cosbig" => TinySystem.Math.Cos(a),
                    "tan" or "tanbig" => TinySystem.Math.Tan(a),
                    "atan2" => TinySystem.Math.Atan2(a, b),
                    "exp" => TinySystem.Math.Exp(a),
                    "log" => TinySystem.Math.Log(a),
                    "log2" => TinySystem.Math.Log(a, 2f),
                    "log10" => TinySystem.Math.Log(a, 10f),
                    "logb" => TinySystem.Math.Log(a, 3f),
                    "pow" => TinySystem.Math.Pow(a, b),
                    _ => TinySystem.Math.Pow(a, 2f),
                };
                int bits = float.IsNaN(r) ? 0x7fc00000 : BitConverter.SingleToInt32Bits(r);
                h = unchecked(h * 31 + bits);
            }
            text.Append(name).Append(" = ").Append(h).Append('\n');
        }
        return text.ToString().TrimEnd('\n');
    }
}
