namespace TinyCs.Tcs2c.Tests;

// Converting values of static types that cannot become strings (class / record / interface / struct / List / Dictionary) to strings is
// outside the subset. C# writes the type name or the list of record members, but Lua writes the table address and
// C does not accept it, so both backends stop with the same TCS1001
public class UnsupportedToStringDifferentialTests
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
        foreach (var run in new Func<object>[]
                 {
                     () => Backends.EmitVm([program], "P"),
                     () => Backends.EmitC([program], "P"),
                 })
        {
            var ex = Assert.Throws<InvalidOperationException>(run);
            Assert.Contains($"error TCS1001: unsupported syntax: {rule}", ex.Message);
        }
    }

    // String conversion of type-argument values is decided by the value, as for object (float uses the same format as .NET, a null T? gives the empty string)
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

    // Interpolation formats for bool / string / char are not used, as in C# (they are not IFormattable)
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
