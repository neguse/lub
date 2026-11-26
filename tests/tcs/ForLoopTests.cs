namespace TinyCs.Tests;

public class ForLoopTests
{
    [Fact]
    public void SimpleFor_Sum()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        // The general for lowering puts the incrementor after the continue label, so
        // the label does not end up at the end of the block. When a local is declared after continue,
        // goto jumps into the local's scope and Lua refuses to load it; this verifies that regression
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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

    [Fact]
    public void GeneralFor_AssignmentInitializers()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    int n = 1234, digits = 0, i = 0, j = 0;
                    for (digits = 0; n > 0; digits++)
                        n -= 500;
                    for (i = 2, j = 5; ; i++)
                    {
                        if (i > 3) break;
                        j += i;
                    }
                    return digits * 100 + j * 10 + i;
                }
            }
            """, "T.Test()");

        Assert.Equal("404", result);
    }

    // ===== do-while =====

    [Fact]
    public void DoWhile_Basic()
    {
        var result = TestHelper.CompileAndRunVm("""
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

    // A C# for condition is re-evaluated on every iteration. When the bound is dynamic,
    // it is not lowered to a Lua numeric for (limit evaluated once) but re-evaluated with while.
    [Fact]
    public void For_BoundVariableMutatedInBody_ReevaluatedEachIteration()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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

    // A C# for control variable is a single variable for the whole loop, and closures share
    // the same variable across all iterations (il-spec §7). A Lua numeric for has a new
    // variable per iteration, so when there is a capture, while desugaring keeps the semantics.
    [Fact]
    public void CapturedForVariable_SharedAcrossIterations()
    {
        var result = TestHelper.CompileAndRunVm("""
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
        var result = TestHelper.CompileAndRunVm("""
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
