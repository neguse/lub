namespace TinyCs.Tests;

public class SwitchTests
{
    // A trailing break of a case body wrapped in a block is the implicit end of the switch.
    // Passing it through as a Lua break would exit the outer loop
    [Fact]
    public void SwitchStatement_BracedCaseInsideLoop_DoesNotBreakLoop()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    int acc = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        switch (i)
                        {
                            case 0: { acc += 1; break; }
                            default: { acc += 10; break; }
                        }
                    }
                    return acc;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("21", result);
    }

    // A conditional early break exits only the switch (skipping the later case bodies)
    [Fact]
    public void SwitchStatement_ConditionalEarlyBreak_ExitsSwitchOnly()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    int acc = 0;
                    for (int i = 0; i < 3; i++)
                    {
                        switch (i)
                        {
                            case 1:
                                if (acc > 0) break;
                                acc += 100;
                                break;
                            default:
                                acc += 1;
                                break;
                        }
                    }
                    return acc;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("2", result);
    }

    // A break of a nested loop inside a case binds to the loop, and the switch continues
    [Fact]
    public void SwitchStatement_BreakInNestedLoop_BindsToLoop()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    int acc = 0;
                    switch (acc)
                    {
                        case 0:
                        {
                            for (int j = 0; j < 5; j++)
                            {
                                if (j == 2) { break; }
                                acc += 1;
                            }
                            acc += 100;
                            break;
                        }
                        default: { acc = -1; break; }
                    }
                    return acc;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("102", result);
    }

    // continue inside a switch binds to the outer loop
    [Fact]
    public void SwitchStatement_ContinueInsideSwitch_ContinuesOuterLoop()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test()
                {
                    int acc = 0;
                    for (int i = 0; i < 5; i++)
                    {
                        switch (i)
                        {
                            case 2:
                                if (i == 2) continue;
                                break;
                            default: { break; }
                        }
                        acc += 1;
                    }
                    return acc;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("4", result);
    }

    [Fact]
    public void SwitchStatement_Basic()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int x = 2;
                    string result = "";
                    switch (x)
                    {
                        case 1:
                            result = "one";
                            break;
                        case 2:
                            result = "two";
                            break;
                        default:
                            result = "other";
                            break;
                    }
                    return result;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("two", result);
    }

    [Fact]
    public void SwitchStatement_Default()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int x = 99;
                    switch (x)
                    {
                        case 1:
                            return "one";
                        case 2:
                            return "two";
                        default:
                            return "other";
                    }
                }
            }
            """,
            "T.Test()");
        Assert.Equal("other", result);
    }

    [Fact]
    public void SwitchStatement_WithEnum()
    {
        var result = TestHelper.CompileAndRunVm("""
            public enum Dir { Up = 0, Down = 1, Left = 2, Right = 3 }
            public class T
            {
                public static string Test()
                {
                    Dir d = Dir.Left;
                    switch (d)
                    {
                        case Dir.Up: return "up";
                        case Dir.Down: return "down";
                        case Dir.Left: return "left";
                        case Dir.Right: return "right";
                        default: return "?";
                    }
                }
            }
            """,
            "T.Test()");
        Assert.Equal("left", result);
    }

    [Fact]
    public void SwitchExpression_Basic()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Test()
                {
                    int x = 3;
                    var msg = x switch
                    {
                        1 => "one",
                        2 => "two",
                        3 => "three",
                        _ => "other"
                    };
                    return msg;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("three", result);
    }

    [Fact]
    public void SwitchExpression_WithEnum()
    {
        var result = TestHelper.CompileAndRunVm("""
            public enum State { Idle = 0, Running = 1, Done = 2 }
            public class T
            {
                public static string Test()
                {
                    State s = State.Running;
                    var name = s switch
                    {
                        State.Idle => "idle",
                        State.Running => "running",
                        State.Done => "done",
                        _ => "unknown"
                    };
                    return name;
                }
            }
            """,
            "T.Test()");
        Assert.Equal("running", result);
    }

    // The switch subject expression is evaluated only once (independent of the number of arms/cases).
    [Fact]
    public void SwitchExpression_GoverningExpression_EvaluatedOnce()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Calls;

                public static int Next()
                {
                    Calls = Calls + 1;
                    return 7;
                }

                public static string Test()
                {
                    var label = Next() switch
                    {
                        > 10 => "big",
                        > 5 => "mid",
                        _ => "small",
                    };
                    return $"{Calls}|{label}";
                }
            }
            """,
            "T.Test()");
        Assert.Equal("1|mid", result);
    }

    [Fact]
    public void SwitchStatement_GoverningExpression_EvaluatedOnce()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Calls;

                public static int Next()
                {
                    Calls = Calls + 1;
                    return 7;
                }

                public static string Test()
                {
                    switch (Next())
                    {
                        case > 10:
                            return $"{Calls}|big";
                        case > 5:
                            return $"{Calls}|mid";
                        default:
                            return $"{Calls}|small";
                    }
                }
            }
            """,
            "T.Test()");
        Assert.Equal("1|mid", result);
    }

    // Pattern labels in a switch statement (relational / or / type) used to
    // produce invalid Lua with an empty condition. They must work, including when mixed with case value labels.
    [Fact]
    public void SwitchStatement_PatternAndConstantLabels_Mixed()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static string Classify(int x)
                {
                    switch (x)
                    {
                        case 1 or 2:
                            return "pair";
                        case 3:
                            return "three";
                        case > 10:
                            return "big";
                        default:
                            return "other";
                    }
                }

                public static string Test() =>
                    Classify(2) + "|" + Classify(3) + "|" + Classify(11) + "|" + Classify(5);
            }
            """,
            "T.Test()");
        Assert.Equal("pair|three|big|other", result);
    }

    [Fact]
    public void SwitchStatement_DeclarationPatternWithWhen_BindsAndMatches()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class Shape { }
            public class Circle
            {
                public int R;
            }
            public class T
            {
                public static string Classify(object s)
                {
                    switch (s)
                    {
                        case Circle c when c.R > 5:
                            return $"big:{c.R}";
                        case Circle:
                            return "circle";
                        default:
                            return "other";
                    }
                }

                public static string Test() =>
                    Classify(new Circle { R = 10 }) + "|"
                    + Classify(new Circle { R = 1 }) + "|"
                    + Classify(new Shape());
            }
            """,
            "T.Test()");
        Assert.Equal("big:10|circle|other", result);
    }

    // An arm with only a type name is syntactically a ConstantPattern, but instead of a value comparison
    // it must work as a metatable comparison (type test).
    [Fact]
    public void SwitchExpression_BareTypePatternArm_MatchesByType()
    {
        var result = TestHelper.CompileAndRunVmTopLevel("""
            object a = new Circle();
            object b = new Square();
            var total = Sorter.Classify(a) * 10 + Sorter.Classify(b);

            public class Shape { }
            public class Circle { }
            public class Square { }

            public class Sorter
            {
                public static int Classify(object s) => s switch
                {
                    Circle => 1,
                    Square => 2,
                    _ => 0,
                };
            }
            """, "total");

        Assert.Equal("12", result);
    }
}
