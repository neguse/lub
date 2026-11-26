namespace TinyCs.Tests;

// The syntax→IL path for method bodies. Bodies that can become IL run on the VM the same as in C#, and bodies that cannot stop with TCS1001.
// The behavior itself is guarded by the existing corpus + differential.
public class IlPipelineTests
{
    [Fact]
    public void ArithmeticControlFlowBody_GoesThroughIl()
    {
        var result = Checker.Check(["""
            public class T
            {
                public static int Compute(int n)
                {
                    var sum = 0;
                    for (int i = 0; i < n; i++)
                    {
                        if (i % 2 == 0) { sum += i; }
                        else { sum -= 1; }
                    }
                    while (sum > 100) { sum = sum / 2; }
                    return sum;
                }
                public static string Fmt(int x) => $"x={x}, half={x / 2}";
            }
            """]);
        Assert.True(result.Success);
    }

    [Fact]
    public void IlEmittedBody_RunsWithSameSemantics()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    var total = 0;
                    var s = "";
                    for (int i = 1; i <= 5; i++)
                    {
                        total += i;
                        if (i == 3) { continue; }
                        s = s + i;
                    }
                    var neg = -7;
                    return $"{total}|{s}|{neg / 2}|{neg % 2}";
                }
            }
            """, "T.Test()");
        Assert.Equal("15|1245|-3|-1", result);
    }

    [Fact]
    public void UnsupportedConstructBody_IsError()
    {
        // Instance method groups are not supported in IL → stop with TCS1001
        var result = Checker.Check(["""
            using System;
            public class T
            {
                public int V;
                public int M() { return V; }
                public object Grab()
                {
                    Func<int> a = M;
                    return a;
                }
            }
            """]);
        Assert.False(result.Success);
        Assert.Single(result.Errors, e => e.Contains("InstanceMethodGroup"));
    }
    [Fact]
    public void ListAndInterpolation_GoThroughIl()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var xs = new List<int> { 3, 1, 2 };
                    xs.Add(10);
                    xs.RemoveAt(0);
                    var d = 0;
                    foreach (var x in xs) { d = d + x; }
                    return $"{xs.Count}:{d}:{xs[0]}";
                }
            }
            """, "T.Test()");
        Assert.Equal("3:13:1", result);
    }

    // Conditional expressions in local initializer / return positions
    [Fact]
    public void Ternary_InLocalAndReturn_IsStatementized()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    var a = 1 > 0 ? "y" : "n";
                    return 2 > 3 ? a + "!" : a + "?";
                }
            }
            """, "T.Test()");
        Assert.Equal("y?", result);
    }

    // switch expressions in statement position
    [Fact]
    public void SwitchExpr_InStatementPositions_IsStatementized()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    var a = 90 switch { > 80 => "A", > 50 => "B", _ => "C" };
                    string b;
                    b = 60 switch { > 80 => "A", > 50 => "B", _ => "C" };
                    return a + b + (10 switch { > 80 => "A", _ => "C" });
                }
            }
            """, "T.Test()");
        Assert.Equal("ABC", result);
    }

    // ?. / ?? / TryGetValue in statement position
    [Fact]
    public void CondAccessAndCoalesce_InStatementPositions_AreStatementized()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<string, int> { { "k", 7 } };
                    int v;
                    var hit = d.TryGetValue("k", out v);
                    int w;
                    var miss = d.TryGetValue("nope", out w);
                    bool? none = null;
                    var b = none ?? true;
                    return $"{hit}:{v}:{miss}:{w}:{b}";
                }
            }
            """, "T.Test()");
        Assert.Equal("True:7:False:0:True", result);
    }

    // TryGetValue in an if condition
    [Fact]
    public void IfCondition_TryGetValue_IsHoisted()
    {
        var result = TestHelper.CompileAndRunVm("""
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<string, int> { { "k", 5 } };
                    int v;
                    var a = "";
                    if (d.TryGetValue("k", out v)) { a = a + v; }
                    if (d.TryGetValue("x", out v)) { a = a + "!"; } else { a = a + v; }
                    return a;
                }
            }
            """, "T.Test()");
        Assert.Equal("50", result);
    }
}
