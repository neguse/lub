namespace TinyCs.Tests;

// T251: char は整数 code unit (il-spec §3)。literal / s[i] / 算術 / 比較 /
// switch / pattern / 文字列化 (連結・補間・ToString・WriteLine) / Char.* /
// string method の char 引数 / foreach (char c in s) / List<char> /
// Dictionary<char, V> / const / default を実 .NET と一致させる
public class CharTests
{
    [Fact]
    public void Char_IsIntegerCodeUnit_AcrossTheSurface()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System;
            using System.Collections.Generic;
            public class T
            {
                public const char Sep = ',';
                public static string Test()
                {
                    var o = "";
                    char c = 'a';
                    char d = (char)(c + 1);
                    int code = c;
                    int diff = 'z' - c;
                    bool range = c >= 'a' && c <= 'z';
                    char u = char.ToUpper(c);
                    string s = "Hello, World 42";
                    char first = s[0];
                    int digits = 0, letters = 0, spaces = 0;
                    foreach (char ch in s)
                    {
                        if (char.IsDigit(ch)) digits++;
                        else if (char.IsLetter(ch)) letters++;
                        else if (char.IsWhiteSpace(ch)) spaces++;
                    }
                    var parts = s.Split(Sep);
                    var idx = s.IndexOf('W');
                    var has = s.Contains('4');
                    var rep = s.Replace('l', 'L');
                    var sw = "";
                    switch (first) { case 'H': sw = "h"; break; case 'x': sw = "x"; break; default: sw = "?"; break; }
                    var kind = c is >= 'a' and <= 'z' ? "lower" : "other";
                    var list = new List<char> { 'x', 'y' };
                    var dict = new Dictionary<char, int>();
                    dict['k'] = 5;
                    char def = default(char);
                    char z = '\0';
                    var sb = "";
                    for (char k = 'a'; k <= 'e'; k++) sb += k;
                    o += (d + ":" + code + ":" + diff + ":" + range + ":" + u + ":" + first + ":" + digits + "," + letters + "," + spaces) + "/";
                    o += (parts.Length + ":" + idx + ":" + has + ":" + rep + ":" + sw + ":" + kind + ":" + list[1] + ":" + dict['k'] + ":" + (int)def + ":" + (z == 0) + ":" + sb + ":" + c.ToString() + ":" + $"{u}{d}" + ":" + (s[1] == 'e') + ":" + char.ToLower('Q') + ":" + char.IsUpper(first) + ":" + s.StartsWith('H') + ":" + s.EndsWith('2')) + "/";
                    return o;
                }
            }
            """, "T.test()");
        Assert.Equal("b:97:25:true:A:H:2,10,2/" +
            "2:7:true:HeLLo, WorLd 42:h:lower:y:5:0:true:abcde:a:Ab:true:q:true:true:true/",
            result);
    }

    // 非 ASCII の char literal は 1 byte に写せないので TCS1001 (string literal で書く)
    [Fact]
    public void NonAsciiCharLiteral_ReportsDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static char Test() => 'é';
            }
            """]);
        Assert.True(result.Success);
        Assert.Single(result.Warnings, w => w.Contains("NonAsciiCharLiteral"));
    }

    [Fact]
    public void CharArithmetic_WrapsLikeInt_AndFormats()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    char c = 'x';
                    int n = c * 2 + 1;
                    char next = (char)(c + 2);
                    var caesar = "";
                    foreach (char ch in "abz")
                        caesar += (char)('a' + (ch - 'a' + 3) % 26);
                    return n + "|" + next + "|" + caesar + "|" + (c > 'a') + "|" + (c == 120);
                }
            }
            """, "T.test()");
        Assert.Equal("241|z|dec|true|true", result);
    }
}
