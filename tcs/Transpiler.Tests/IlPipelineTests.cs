namespace TinyCs.Tests;

// T214 (M1): method body の syntax→IL→Lua 経路。ストラングラー方式のため
// 「IL 対応構文だけの method は IL 経由で emit される」ことと
// 「未対応構文を含む method は legacy へ fallback して従来出力になる」ことを
// 両方ロックする。挙動不変そのものは既存 corpus + differential が守る。
public class IlPipelineTests
{
    [Fact]
    public void ArithmeticControlFlowBody_GoesThroughIl()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
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
        Assert.Equal(2, result.IlBodies);
        Assert.Equal(0, result.LegacyBodies);
    }

    [Fact]
    public void IlEmittedBody_RunsWithSameSemantics()
    {
        var result = TestHelper.TranspileAndRun("""
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
    public void UnsupportedConstructBody_FallsBackToLegacy()
    {
        // method group 参照 (bare 識別子の delegate 化) は IL 未対応 → legacy
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            public class T
            {
                public static void M() { }
                public static object Grab()
                {
                    Action a = M;
                    return a;
                }
            }
            """]);
        Assert.True(result.Success);
        Assert.Equal(1, result.IlBodies); // M は空 body で IL
        Assert.Equal(1, result.LegacyBodies);
    }

    [Fact]
    public void MixedClass_SplitsPerMethod()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static int Sum(List<int> xs)
                {
                    var sum = 0;
                    foreach (var x in xs) { sum += x; }
                    return sum;
                }
                public static object Pick(List<int> xs)
                {
                    // method group 参照は未対応 → この method だけ legacy
                    Func<List<int>, int> f = Sum;
                    return f;
                }
            }
            """]);
        Assert.True(result.Success);
        Assert.Equal(1, result.IlBodies);
        Assert.Equal(1, result.LegacyBodies);
    }

    [Fact]
    public void ListAndInterpolation_GoThroughIl()
    {
        var result = TestHelper.TranspileAndRun("""
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

    // T225: local 初期化 / return 位置の条件式は IIFE でなく if 文へ
    [Fact]
    public void Ternary_InLocalAndReturn_IsStatementized()
    {
        var lua = Transpiler.Transpile(["""
            public class T
            {
                public static int Pick(bool c)
                {
                    var x = c ? 10 : 20;
                    return x > 15 ? 1 : 0;
                }
            }
            """]);
        Assert.DoesNotContain("(function()", lua);
        var result = TestHelper.TranspileAndRun("""
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
}
