namespace TinyCs.Tests;

public class SwitchTests
{
    // case 本体を block で包んだ末尾 break は switch の暗黙終端。
    // Lua の break として素通しすると外側 loop を脱出してしまう
    [Fact]
    public void SwitchStatement_BracedCaseInsideLoop_DoesNotBreakLoop()
    {
        var result = TestHelper.TranspileAndRun("""
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

    // 条件付き早期 break は switch だけを抜ける (後続 case 本体をスキップ)
    [Fact]
    public void SwitchStatement_ConditionalEarlyBreak_ExitsSwitchOnly()
    {
        var result = TestHelper.TranspileAndRun("""
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

    // case 内の nested loop の break は loop に束縛され switch は続行する
    [Fact]
    public void SwitchStatement_BreakInNestedLoop_BindsToLoop()
    {
        var result = TestHelper.TranspileAndRun("""
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

    // switch 内の continue は外側 loop に束縛される
    [Fact]
    public void SwitchStatement_ContinueInsideSwitch_ContinuesOuterLoop()
    {
        var result = TestHelper.TranspileAndRun("""
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
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("two", result);
    }

    [Fact]
    public void SwitchStatement_Default()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("other", result);
    }

    [Fact]
    public void SwitchStatement_WithEnum()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("left", result);
    }

    [Fact]
    public void SwitchExpression_Basic()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("three", result);
    }

    [Fact]
    public void SwitchExpression_WithEnum()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("running", result);
    }

    // switch の対象式は一度だけ評価される (arm/case 数に依存しない)。
    [Fact]
    public void SwitchExpression_GoverningExpression_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("1|mid", result);
    }

    [Fact]
    public void SwitchStatement_GoverningExpression_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("1|mid", result);
    }

    // switch statement のパターンラベル (relational / or / 型) は
    // 従来空条件の不正 Lua になっていた。case 値ラベルとの混在も含めて動くこと。
    [Fact]
    public void SwitchStatement_PatternAndConstantLabels_Mixed()
    {
        var result = TestHelper.TranspileAndRun("""
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
            "T.test()");
        Assert.Equal("pair|three|big|other", result);
    }

    [Fact]
    public void SwitchStatement_DeclarationPatternWithWhen_BindsAndMatches()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Shape { }
            public class Circle : Shape
            {
                public int R;
            }
            public class T
            {
                public static string Classify(Shape s)
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
            "T.test()");
        Assert.Equal("big:10|circle|other", result);
    }

    // 型名だけの arm は syntax 上 ConstantPattern になるが、値比較ではなく
    // metatable 比較 (型判定) として動くこと。
    [Fact]
    public void SwitchExpression_BareTypePatternArm_MatchesByType()
    {
        var result = TestHelper.TranspileAndRun("""
            Shape a = new Circle();
            Shape b = new Square();
            var total = Sorter.Classify(a) * 10 + Sorter.Classify(b);

            public class Shape { }
            public class Circle : Shape { }
            public class Square : Shape { }

            public class Sorter
            {
                public static int Classify(Shape s) => s switch
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
