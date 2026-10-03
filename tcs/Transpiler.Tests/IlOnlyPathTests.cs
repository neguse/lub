namespace TinyCs.Tests;

// T250: legacy visitor 廃止で IL 経路が唯一になった際に IL 化した構文。
// 以前は legacy fallback で動いていたもの (実 .NET と一致させる)
public class IlOnlyPathTests
{
    [Fact]
    public void ElseIf_WithIsPatternDesignation_NestsIntoElse()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Shape { }
            public class Circle : Shape { public int R; }
            public class Rect : Shape { public int W; public int H; }
            public class T
            {
                public static int Area(Shape s)
                {
                    if (s == null) return -1;
                    else if (s is Circle c) return c.R * c.R * 3;
                    else if (s is Rect r && r.W > 0) return r.W * r.H;
                    else return 0;
                }
                public static string Test()
                {
                    var c = new Circle(); c.R = 2;
                    var r = new Rect(); r.W = 3; r.H = 4;
                    var z = new Rect();
                    return Area(null) + "|" + Area(c) + "|" + Area(r) + "|" + Area(z);
                }
            }
            """, "T.test()");
        Assert.Equal("-1|12|12|0", result);
    }

    [Fact]
    public void DiscardAndAssignmentExpressions_Evaluate()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Calls;
                public static int Bump() { Calls++; return Calls; }
                public static string Test()
                {
                    _ = Bump();
                    _ = Bump();
                    int i = 0;
                    int x = 3;
                    var ok = x >= 0 && (i = x * 2) >= 0;
                    var arr = new int[4];
                    int k;
                    arr[k = 1] = k + 10;
                    int a, b;
                    a = b = 7;
                    return Calls + "|" + ok + "|" + i + "|" + arr[1] + "|" + a + "|" + b;
                }
            }
            """, "T.test()");
        Assert.Equal("2|true|6|11|7|7", result);
    }

    [Fact]
    public void ConditionalAccess_ListClearAndFirstOrDefault()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;
            public class T
            {
                public static string Test()
                {
                    List<int> xs = new List<int> { 5, 6, 7 };
                    List<int> none = null;
                    var first = xs?.FirstOrDefault();
                    var last = xs?.LastOrDefault(v => v < 7);
                    var missing = none?.FirstOrDefault();
                    xs?.Clear();
                    none?.Clear();
                    return first + "|" + last + "|" + (missing == null) + "|" + xs.Count;
                }
            }
            """, "T.test()");
        Assert.Equal("5|6|true|0", result);
    }

    [Fact]
    public void ExtensionMethod_OnStructReceiver_UsesCopy()
    {
        var result = TestHelper.TranspileAndRun("""
            public struct Vec { public int X; }
            public static class VecExt
            {
                public static int Bumped(this Vec v, int by) { v.X += by; return v.X; }
            }
            public class T
            {
                public static string Test()
                {
                    var v = new Vec(); v.X = 1;
                    var r = v.Bumped(5);
                    return r + "|" + v.X;
                }
            }
            """, "T.test()");
        Assert.Equal("6|1", result);
    }

    [Fact]
    public void Lock_BodyExecutes_WithDiagnostic()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class Gate { }
            public class T
            {
                public static Gate G = new Gate();
                public static int Test()
                {
                    var n = 1;
                    lock (G) { n += 41; }
                    return n;
                }
            }
            """]);
        Assert.True(result.Success);
        Assert.Single(result.Warnings, w => w.Contains("LockStatement"));
        Assert.Equal("42", TestHelper.RunLua($"{result.Lua}\nprint(T.test())").Trim());
    }

    [Fact]
    public void Switch_EarlyBreakInsideLoop_WithContinue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    var s = "";
                    for (int i = 0; i < 6; i++)
                    {
                        switch (i % 3)
                        {
                            case 0:
                                if (i > 2) break;
                                s += "a";
                                break;
                            case 1:
                                if (i == 4) continue;
                                s += "b";
                                break;
                            default:
                                s += "c";
                                break;
                        }
                        s += ".";
                    }
                    return s;
                }
            }
            """, "T.test()");
        Assert.Equal("a.b.c..c.", result);
    }
}
