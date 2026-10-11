namespace TinyCs.Tcs2c.Tests;

// 補間の D 精度指定 (符号を除いた桁数)、raw 補間文字列の literal brace、
// int.TryParse の受理する書式 (NumberStyles.Integer: 前後の空白、符号、
// 10 進数字のみ)。期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Interpolation_DPrecisionRawBracesAndIntTryParse_MatchDotnet()
    {
        var output = Backends.AssertParity(""""
            using System;
            public class P
            {
                public static string T(string s)
                {
                    bool ok = int.TryParse(s, out int v);
                    return (ok ? "1:" : "0:") + v;
                }
                public static void Main()
                {
                    int neg = -12;
                    int pos = 12;
                    int min = -2147483647 - 1;
                    Console.WriteLine($"{neg:D4}|{neg:D3}|{neg:D5}|{pos:D4}|{0:D4}|{neg:D2}|{min:D11}|{min:D10}|{neg,7:D4}|{neg:D}");
                    int n = 7;
                    string s = $$$"""{{literal}} {{{n}}}""";
                    Console.WriteLine(s + " / " + s.Length + " / " + (s == "{{literal}} 7" ? 1 : 0));
                    string two = $$"""{literal} {{n}} }""";
                    Console.WriteLine(two + " / " + two.Length);
                    string multi = $$$"""
                        {{a}} {{{n}}}
                        """;
                    Console.WriteLine(multi + " / " + multi.Length);
                    Console.WriteLine($"{{x}} {n} }}" + " " + $@"{{v}}{n}");
                    Console.WriteLine(T("1.0") + " " + T(" ") + " " + T("") + " " + T("12") + " " + T(" -34 ") + " " + T("+5") + " " + T("\t7\n") + " " + T("0x10") + " " + T("1e3") + " " + T("- 5") + " " + T("5-") + " " + T("007"));
                    Console.WriteLine(T("2147483647") + " " + T("2147483648") + " " + T("-2147483648") + " " + T("-2147483649") + " " + T("12a") + " " + T("a12") + " " + T("1 2") + " " + T("-") + " " + T("+") + " " + T("--1") + " " + T("00000000000000000001") + " " + T(".5") + " " + T("5."));
                }
            }
            """", "P");
        Assert.Equal("""
            -0012|-012|-00012|0012|0000|-12|-02147483648|-2147483648|  -0012|-12
            {{literal}} 7 / 13 / 1
            {literal} 7 } / 13
            {{a}} 7 / 7
            {x} 7 } {v}7
            0:0 0:0 0:0 1:12 1:-34 1:5 1:7 0:0 0:0 0:0 0:0 1:7
            1:2147483647 0:0 1:-2147483648 0:0 0:0 0:0 0:0 0:0 0:0 0:0 1:1 0:0 0:0
            """.ReplaceLineEndings("\n"), output);
    }
}
