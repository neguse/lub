namespace TinyCs.Tests;

// T180: 値型・string の型パターンは Lua 側に型 table が無く、
// `getmetatable(x) == int` (未定義 global = nil 比較) だと nil がマッチする。
// type() 判定 (number/boolean/string) を emit する。
public class TypePatternTests
{
    [Fact]
    public void IsInt_OnNullable_MatchesOnlyValues()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int? none = null;
                    int? some = 7;
                    var a = none is int;
                    int b;
                    if (some is int x) b = x; else b = -1;
                    return $"{a}|{b}";
                }
            }
            """, "T.Test()");
        Assert.Equal("false|7", result);
    }

    [Fact]
    public void IsBoolAndString_MatchByLuaType()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    bool? flag = false;
                    string? name = null;
                    string? given = "ok";
                    string a;
                    if (flag is bool b) a = b ? "t" : "f"; else a = "none";
                    var c = name is string ? "str" : "nil";
                    string d;
                    if (given is string s) d = s; else d = "nil";
                    return $"{a}|{c}|{d}";
                }
            }
            """, "T.Test()");
        Assert.Equal("f|nil|ok", result);
    }

    [Fact]
    public void SwitchExpression_ValueTypeArm_DoesNotMatchNull()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Classify(int? n) => n switch
                {
                    int v => v,
                    _ => -1,
                };

                public static string Test() =>
                    $"{Classify(5)}|{Classify(null)}";
            }
            """, "T.Test()");
        Assert.Equal("5|-1", result);
    }

    // T181: 式文脈 (ternary / 複合条件 / lambda) の is-pattern designation 束縛
    [Fact]
    public void IsPattern_InTernary_BindsDesignation()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int? some = 7;
                    int? none = null;
                    var a = some is int x ? x : -1;
                    var b = none is int y ? y : -1;
                    return $"{a}|{b}";
                }
            }
            """, "T.Test()");
        Assert.Equal("7|-1", result);
    }

    [Fact]
    public void IsPattern_InCompoundCondition_BindsDesignation()
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
                    if (s is Circle c && c.R > 5) return $"big:{c.R}";
                    return "other";
                }

                public static string Test() =>
                    Classify(new Circle { R = 9 }) + "|" + Classify(new Shape());
            }
            """, "T.Test()");
        Assert.Equal("big:9|other", result);
    }

    [Fact]
    public void IsPattern_InLambda_BindsDesignation()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;
            using System.Linq;

            public class Shape { }
            public class Circle : Shape
            {
                public int R;
            }
            public class T
            {
                public static int Test()
                {
                    var shapes = new List<Shape>
                    {
                        new Circle { R = 1 },
                        new Shape(),
                        new Circle { R = 9 },
                    };
                    return shapes.Count(o => o is Circle c && c.R > 2);
                }
            }
            """, "T.Test()");
        Assert.Equal("1", result);
    }

    [Fact]
    public void ClassPattern_StillUsesMetatable()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Shape { }
            public class Circle : Shape { }
            public class T
            {
                public static string Test()
                {
                    Shape s = new Circle();
                    Shape n = null;
                    var a = s is Circle ? "circle" : "other";
                    var b = n is Circle ? "circle" : "none";
                    return $"{a}|{b}";
                }
            }
            """, "T.Test()");
        Assert.Equal("circle|none", result);
    }
}
