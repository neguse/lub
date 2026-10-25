namespace TinyCs.Tcs2c.Tests;

// 文字列にできない静的型 (class / record / interface / struct / List / Dictionary) の文字列化は
// サブセット外。C# は型名や record の member の一覧を書くが、Lua は table のアドレスを書き、
// C は受け付けないので、両 backend とも同じ TCS1001 で止まる
public partial class DifferentialTests
{
    [Theory]
    [InlineData("Console.WriteLine(r);", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine(\"x\" + r);", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine($\"{r}\");", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine(r.ToString());", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine($\"{m}\");", "UnsupportedToString(M)")]
    [InlineData("Console.WriteLine(m.ToString());", "UnsupportedToString(M)")]
    [InlineData("Console.WriteLine(new List<int>());", "UnsupportedToString(System.Collections.Generic.List<int>)")]
    [InlineData("Console.WriteLine(string.Join(\",\", new List<R> { r }));", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine(string.Join(\",\", new M[] { m }));", "UnsupportedToString(M)")]
    [InlineData("Console.WriteLine(string.Join(\",\", new Dictionary<int, R>().Values));", "UnsupportedToString(R)")]
    [InlineData("foreach (var kv in new Dictionary<int, int> { { 1, 2 } }) Console.WriteLine(kv);", "UnsupportedToString(System.Collections.Generic.KeyValuePair<int, int>)")]
    [InlineData("foreach (var kv in new Dictionary<int, int> { { 1, 2 } }) Console.WriteLine(\"x\" + kv);", "UnsupportedToString(System.Collections.Generic.KeyValuePair<int, int>)")]
    [InlineData("R? n = r; Console.WriteLine(n?.ToString());", "UnsupportedToString(R)")]
    [InlineData("Console.WriteLine(new C().Show());", "UnsupportedToString(C)")]
    public void UnsupportedToString_BothBackendsStopWithDiagnostic(string statement, string rule)
    {
        var program = $$"""
            using System;
            using System.Collections.Generic;
            public record R(int A);
            public struct M { public int X; }
            public class C { public string Show() => ToString(); }
            public static class P
            {
                public static void Main()
                {
                    var r = new R(1);
                    var m = new M();
                    {{statement}}
                }
            }
            """;
        foreach (var run in new Func<string>[]
                 {
                     () => Backends.RunLua([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }

    // 型引数の値の文字列化は object と同じく値で決める (float は .NET と同じ書式、T? の null は空文字列)
    [CFact]
    public void TypeParameterToString_FollowsValue()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class G
            {
                public static string S<T>(T x) => "" + x + "|" + $"{x}";
            }
            public static class P
            {
                public static void Main()
                {
                    Console.WriteLine(G.S(1e10f) + "|" + G.S(0.1f) + "|" + G.S<int?>(null) + "|" + G.S(3) + "|" + G.S("s"));
                }
            }
            """, "P");
        Assert.Equal("1E+10|1E+10|0.1|0.1|||3|3|s|s", output);
    }

    // bool / string / char の補間の書式は C# と同じく使わない (IFormattable でない)
    [CFact]
    public void InterpolationFormat_IgnoredForBoolStringAndChar()
    {
        var output = Backends.AssertParity("""
            using System;
            public static class P
            {
                public static void Main()
                {
                    bool b = true;
                    bool? n = false;
                    string s = "s";
                    char c = 'c';
                    Console.WriteLine($"[{b:x}][{n:D}][{s:x}][{c:x}][{b,6:x}]");
                }
            }
            """, "P");
        Assert.Equal("[True][False][s][c][  True]", output);
    }
}
