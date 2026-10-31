namespace TinyCs.Tcs2c.Tests;

// delegate の同一性と合成はサブセット外: Lua は関数の同一性、C は変換のたびに作る closure で、C# と結果を揃えられない。
// 両 backend とも同じ診断で止まる。null との比較、呼び出し、代入は残す
public partial class DifferentialTests
{
    [Theory]
    [InlineData("Console.WriteLine(a == b);", "TCS1001", "DelegateEquality")]
    [InlineData("Console.WriteLine(a != b);", "TCS1001", "DelegateEquality")]
    [InlineData("Console.WriteLine(a == F);", "TCS1001", "DelegateEquality")]
    [InlineData("object o = b; Console.WriteLine(a == o);", "TCS1001", "DelegateEquality")]
    [InlineData("Console.WriteLine(a.Equals(b));", "TCS1002", "Equals")]
    [InlineData("c += G;", "TCS1001", "DelegateCombine")]
    [InlineData("c -= G;", "TCS1001", "DelegateCombine")]
    [InlineData("c = c + G;", "TCS1001", "DelegateCombine")]
    [InlineData("c = c - G;", "TCS1001", "DelegateCombine")]
    public void DelegateOperations_BothBackendsStopWithDiagnostic(string statement, string id, string rule)
    {
        var program = $$"""
            using System;
            public static class P
            {
                static bool F(int x) { return x > 0; }
                static void G() { }
                public static void Main()
                {
                    Func<int, bool> a = F;
                    Func<int, bool> b = F;
                    Action c = G;
                    {{statement}}
                }
            }
            """;
        var lua = Transpiler.TranspileWithDiagnostics([program]);
        Assert.Contains(lua.Errors, e => e.Contains(id) && e.Contains(rule));
        var c = IlExport.Export([program]);
        Assert.Contains(c.Diagnostics, d => d.Contains(id) && d.Contains(rule));
    }

    // 期待値は同じ program を CoreCLR で実行した stdout
    [CFact]
    public void DelegateOperations_NullComparisonAndCallsRemain()
    {
        var output = Backends.AssertParity("""
            using System;
            public class H
            {
                public Func<int, int> F;
                public int Run(int v) { return F != null ? F(v) : -1; }
            }
            public static class P
            {
                static int Twice(int x) { return x * 2; }
                public static void Main()
                {
                    var h = new H();
                    Console.WriteLine(h.Run(3));
                    Console.WriteLine(h.F == null);
                    Console.WriteLine(null == h.F);
                    h.F = Twice;
                    Console.WriteLine(h.Run(3));
                    Console.WriteLine(h.F != null);
                    h.F = x => x + 1;
                    Console.WriteLine(h.Run(3));
                    Func<int, int> g = default;
                    Console.WriteLine(g == default);
                }
            }
            """, "P");
        Assert.Equal("""
            -1
            True
            True
            6
            True
            4
            True
            """.ReplaceLineEndings("\n"), output);
    }
}
