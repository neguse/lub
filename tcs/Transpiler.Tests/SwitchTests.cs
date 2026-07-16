namespace TinyCs.Tests;

public class SwitchTests
{
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
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
            "T.Test()");
        Assert.Equal("running", result);
    }

    // T140: switch の対象式は一度だけ評価される (arm/case 数に依存しない)。
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
            "T.Test()");
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
            "T.Test()");
        Assert.Equal("1|mid", result);
    }

    // T140: switch statement のパターンラベル (relational / or / 型) は
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
            "T.Test()");
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
            "T.Test()");
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
