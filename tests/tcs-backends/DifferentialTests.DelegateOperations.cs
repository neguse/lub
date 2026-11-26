namespace TinyCs.Tcs2c.Tests;

// Delegate identity and composition are outside the subset: Lua uses function identity and C a closure created at every conversion, so the results cannot match C#.
// Both backends stop with the same diagnostic. Comparison with null, invocation, and assignment are kept
public class DelegateOperationsDifferentialTests
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
        var vm = Assert.Throws<InvalidOperationException>(() => Backends.EmitVm([program], "P"));
        Assert.Contains(vm.Message.Split('\n'), e => e.Contains(id) && e.Contains(rule));
        var c = IlExport.Export([program]);
        Assert.Contains(c.Diagnostics, d => d.Contains(id) && d.Contains(rule));
    }

    // The expected values are the stdout of running the same program on CoreCLR
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
