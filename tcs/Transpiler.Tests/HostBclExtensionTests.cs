namespace TinyCs.Tests;

// Lua 標準ライブラリを C# 側の stub (os / utf8 / string) で直接呼ぶ
// 代わりに、同じ C# が実 .NET でも通る BCL API を allowlist に足す。
//   Environment.GetEnvironmentVariable → os.getenv
//   int.Parse / float.Parse            → math.tointeger(tonumber) / tonumber
//   foreach (var r in s.EnumerateRunes()) + r.Value → utf8.codes
//   s[i] / (int)s[i] / (int)'a'        → string.sub / string.byte
public class HostBclExtensionTests
{
    [Fact]
    public void Environment_GetEnvironmentVariable_MapsToOsGetenv()
    {
        Environment.SetEnvironmentVariable("TCS_HOST_BCL_TEST", "from-env");
        var result = TestHelper.TranspileAndRun("""
            using System;
            public static class T
            {
                public static string Test()
                {
                    var v = Environment.GetEnvironmentVariable("TCS_HOST_BCL_TEST") ?? "none";
                    var missing = Environment.GetEnvironmentVariable("TCS_HOST_BCL_MISSING") ?? "none";
                    return v + ":" + missing;
                }
            }
            """, "T.test()", differential: false);
        Assert.Equal("from-env:none", result);
    }

    [Fact]
    public void Parse_IntAndDouble()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class T
            {
                public static string Test()
                {
                    int a = int.Parse("42");
                    float b = float.Parse("2.5");
                    float c = float.Parse("1.25");
                    return (a + 1) + ":" + (b * 2) + ":" + (c * 4) + ":" + (a / 5);
                }
            }
            """, "T.test()", differential: false); // 5.0 と 5 の表記差 (既知)
        Assert.Equal("43:5:5:8", result);
    }

    [Fact]
    public void EnumerateRunes_IteratesCodepoints()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Text;
            public static class T
            {
                public static int Sum(string s)
                {
                    var sum = 0;
                    foreach (var r in s.EnumerateRunes())
                        sum += r.Value;
                    return sum;
                }
                public static int Count(string s)
                {
                    var n = 0;
                    foreach (Rune r in s.EnumerateRunes())
                        n++;
                    return n;
                }
            }
            """, "T.sum('a\\u{e9}') .. ',' .. T.count('\\u{3042}\\u{3044}z')",
            differential: false);
        Assert.Equal("330,3", result);
    }

    [Fact]
    public void StringIndexer_AndCharToInt()
    {
        var result = TestHelper.TranspileAndRun("""
            public static class T
            {
                public static string Test()
                {
                    var s = "Ab-9";
                    var dash = s[2] == '-';
                    var code = (int)s[0];
                    var digit = (int)s[3] - (int)'0';
                    var ch = s[1];
                    return (dash ? "y" : "n") + ":" + code + ":" + digit + ":" + ch;
                }
            }
            """, "T.test()", differential: false);
        Assert.Equal("y:65:9:b", result);
    }

    [Fact]
    public void HostBclApis_AreNotFlaggedAsUnsupported()
    {
        var source = """
            using System;
            using System.Text;
            public static class T
            {
                public static int Test(string s)
                {
                    var v = Environment.GetEnvironmentVariable("X");
                    var n = int.Parse("1") + (int)float.Parse("2");
                    foreach (var r in s.EnumerateRunes()) n += r.Value;
                    return n + (int)s[0];
                }
            }
            """;
        var result = Transpiler.TranspileWithDiagnostics([source]);
        Assert.True(result.Success, string.Join("\n", result.Errors));
        Assert.Empty(result.Warnings);
    }
}
