namespace TinyCs.Tests;

public class ForLoopTests
{
    [Fact]
    public void SimpleFor_Sum()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    for (int i = 0; i < n; i++)
                    {
                        total = total + i;
                    }
                    return total;
                }
            }
            """,
            "Loop.Sum(5)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void SimpleFor_StartFromOne()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Sum(int n)
                {
                    var total = 0;
                    for (int i = 1; i <= n; i++)
                    {
                        total = total + i;
                    }
                    return total;
                }
            }
            """,
            "Loop.Sum(10)");
        Assert.Equal("55", result);
    }

    [Fact]
    public void For_WithBreak()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int FirstOver(int limit)
                {
                    for (int i = 0; i < 100; i++)
                    {
                        if (i > limit) { return i; }
                    }
                    return -1;
                }
            }
            """,
            "Loop.FirstOver(5)");
        Assert.Equal("6", result);
    }

    [Fact]
    public void For_Nested()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Loop
            {
                public static int Multiply(int rows, int cols)
                {
                    var count = 0;
                    for (int r = 0; r < rows; r++)
                    {
                        for (int c = 0; c < cols; c++)
                        {
                            count = count + 1;
                        }
                    }
                    return count;
                }
            }
            """,
            "Loop.Multiply(3, 4)");
        Assert.Equal("12", result);
    }

    // ===== continue =====

    [Fact]
    public void Continue_ForLoop()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int sum = 0;
                    for (int i = 0; i < 10; i++)
                    {
                        if (i % 2 == 0) continue;
                        sum = sum + i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("25", result); // 1+3+5+7+9
    }

    [Fact]
    public void Continue_WhileLoop()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int sum = 0;
                    int i = 0;
                    while (i < 5)
                    {
                        i = i + 1;
                        if (i == 3) continue;
                        sum = sum + i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("12", result); // 1+2+4+5
    }

    [Fact]
    public void Continue_GeneralFor_LocalDeclaredAfterContinue()
    {
        // 一般 for lowering は continue label の後に incrementor が続くため、
        // label が block 末尾に来ない。continue より後で宣言した local がある場合、
        // goto が local スコープへ飛び込む形になり Lua が load を拒否する回帰の検証
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Limit = 5;
                public static int Test()
                {
                    int sum = 0;
                    for (int i = 0; i < Limit; i++)
                    {
                        if (i == 2) continue;
                        int doubled = i * 2;
                        sum = sum + doubled;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("16", result); // (0+1+3+4)*2
    }

    [Fact]
    public void Continue_ForEach()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;
            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4, 5 };
                    int sum = 0;
                    foreach (var x in list)
                    {
                        if (x == 3) continue;
                        sum = sum + x;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("12", result); // 1+2+4+5
    }

    // ===== general for incrementors =====

    [Fact]
    public void GeneralFor_PostDecrementIncrementor()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var sum = 0;
                    for (int i = 3; i > 0; i--)
                    {
                        sum += i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");

        Assert.Equal("6", result);
    }

    [Fact]
    public void GeneralFor_AddAssignmentIncrementor()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var sum = 0;
                    for (int i = 0; i < 6; i += 2)
                    {
                        sum += i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");

        Assert.Equal("6", result);
    }

    [Fact]
    public void GeneralFor_MultipleIncrementors()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var sum = 0;
                    for (int i = 0, j = 0; i < 3; i++, j += 2)
                    {
                        sum += j;
                    }
                    return sum;
                }
            }
            """, "T.Test()");

        Assert.Equal("6", result);
    }

    // ===== do-while =====

    [Fact]
    public void DoWhile_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int i = 0;
                    do
                    {
                        i = i + 1;
                    } while (i < 5);
                    return i;
                }
            }
            """, "T.Test()");
        Assert.Equal("5", result);
    }

    // T144: C# の for 条件は毎 iteration 再評価される。bound が動的な場合は
    // Lua numeric for (limit 一回評価) に落とさず while で再評価する。
    [Fact]
    public void For_BoundVariableMutatedInBody_ReevaluatedEachIteration()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var n = 3;
                    var count = 0;
                    for (int i = 0; i < n; i++)
                    {
                        count = count + 1;
                        if (i == 0) n = 5;
                    }
                    return count;
                }
            }
            """, "T.Test()");
        Assert.Equal("5", result);
    }

    [Fact]
    public void For_MethodCallBound_EvaluatedEachIteration()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Calls;

                public static int Limit()
                {
                    Calls = Calls + 1;
                    return 3;
                }

                public static string Test()
                {
                    var sum = 0;
                    for (int i = 0; i < Limit(); i++)
                    {
                        sum = sum + i;
                    }
                    return $"{Calls}|{sum}";
                }
            }
            """, "T.Test()");
        Assert.Equal("4|3", result);
    }

    [Fact]
    public void For_ListCountBound_TracksShrinkingList()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class T
            {
                public static int Test()
                {
                    var list = new List<int> { 1, 2, 3, 4 };
                    var iterations = 0;
                    for (int i = 0; i < list.Count; i++)
                    {
                        iterations = iterations + 1;
                        if (i == 0) list.RemoveAt(0);
                    }
                    return iterations;
                }
            }
            """, "T.Test()");
        Assert.Equal("3", result);
    }

    [Fact]
    public void For_LoopVariableAssignedInBody_AffectsIteration()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var sum = 0;
                    for (int i = 0; i < 6; i++)
                    {
                        sum = sum + i;
                        if (i == 2) i = 4;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("8", result);
    }

    [Fact]
    public void For_StableLocalBound_StillWorks()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    var n = 4;
                    var sum = 0;
                    for (int i = 0; i < n; i++)
                    {
                        sum = sum + i;
                    }
                    return sum;
                }
            }
            """, "T.Test()");
        Assert.Equal("6", result);
    }

    [Fact]
    public void DoWhile_ExecutesAtLeastOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test()
                {
                    int i = 10;
                    do
                    {
                        i = i + 1;
                    } while (i < 5);
                    return i;
                }
            }
            """, "T.Test()");
        Assert.Equal("11", result); // Executes once even though 10 >= 5
    }

    // T221: C# の for 制御変数はループ全体で 1 個であり、closure は全反復で
    // 同じ変数を共有する (il-spec §7)。Lua numeric for は反復ごとに新しい
    // 変数のため、捕捉がある場合は while 脱糖で意味論を保つ。
    [Fact]
    public void CapturedForVariable_SharedAcrossIterations()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var fs = new List<Func<int>>();
                    for (int i = 0; i < 3; i++)
                    {
                        fs.Add(() => i);
                    }
                    var s = "";
                    foreach (var f in fs)
                    {
                        s = s + f() + ",";
                    }
                    return s;
                }
            }
            """, "T.Test()");
        Assert.Equal("3,3,3,", result);
    }

    [Fact]
    public void CapturedForeachVariable_FreshPerIteration()
    {
        var result = TestHelper.TranspileAndRun("""
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static string Test()
                {
                    var src = new List<int> { 10, 20 };
                    var fs = new List<Func<int>>();
                    foreach (var v in src)
                    {
                        fs.Add(() => v);
                    }
                    var s = "";
                    foreach (var f in fs)
                    {
                        s = s + f() + ",";
                    }
                    return s;
                }
            }
            """, "T.Test()");
        Assert.Equal("10,20,", result);
    }
}
