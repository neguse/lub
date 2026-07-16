namespace TinyCs.Tests;

// T147: custom property (body 付き accessor / expression-bodied) の読み書きは
// 生成済み get_/set_ を呼ぶ。auto property は raw field のまま。
public class PropertyAccessorTests
{
    private const string TempClass = """
        public class Temp
        {
            public int Raw;

            public int Celsius
            {
                get { return Raw / 10; }
                set { Raw = value * 10; }
            }
        }
        """;

    [Fact]
    public void CustomProperty_ReadWrite_UsesAccessors()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static string Test()
                {
                    var t = new Temp();
                    t.Celsius = 25;
                    var c = t.Celsius;
                    return $"{t.Raw}|{c}";
                }
            }
            """, "T.Test()");
        Assert.Equal("250|25", result);
    }

    [Fact]
    public void CustomProperty_CompoundAssignment_ReadsAndWritesViaAccessors()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static string Test()
                {
                    var t = new Temp();
                    t.Celsius = 20;
                    t.Celsius += 5;
                    return $"{t.Raw}";
                }
            }
            """, "T.Test()");
        Assert.Equal("250", result);
    }

    [Fact]
    public void ExpressionBodiedProperty_ReadsViaGetter()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class Box
            {
                public int N;

                public int Doubled => N * 2;
            }
            public class T
            {
                public static int Test()
                {
                    var b = new Box();
                    b.N = 21;
                    return b.Doubled;
                }
            }
            """, "T.Test()");
        Assert.Equal("42", result);
    }

    [Fact]
    public void CustomProperty_SideEffectReceiverCompound_ReceiverOnce()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static int Calls;
                public static Temp Shared;

                public static Temp Get()
                {
                    Calls = Calls + 1;
                    return Shared;
                }

                public static string Test()
                {
                    Shared = new Temp();
                    Shared.Celsius = 20;
                    Get().Celsius += 5;
                    return $"{Calls}|{Shared.Raw}";
                }
            }
            """, "T.Test()");
        Assert.Equal("1|250", result);
    }

    [Fact]
    public void CustomProperty_ObjectInitializer_UsesSetter()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static int Test()
                {
                    var t = new Temp { Celsius = 3 };
                    return t.Raw;
                }
            }
            """, "T.Test()");
        Assert.Equal("30", result);
    }

    [Fact]
    public void CustomProperty_ImplicitThisAccess_UsesAccessors()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class Counter
            {
                public int Raw;

                public int Value
                {
                    get { return Raw + 1; }
                    set { Raw = value - 1; }
                }

                public int Bump()
                {
                    Value = 10;
                    return Value;
                }
            }
            public class T
            {
                public static string Test()
                {
                    var c = new Counter();
                    var v = c.Bump();
                    return $"{c.Raw}|{v}";
                }
            }
            """, "T.Test()");
        Assert.Equal("9|10", result);
    }

    [Fact]
    public void CustomProperty_ConditionalAccess_UsesGetter()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static string Test()
                {
                    Temp t = new Temp();
                    t.Celsius = 7;
                    Temp none = null;
                    var a = t?.Celsius;
                    var b = none?.Celsius;
                    return $"{a ?? -1}|{b ?? -1}";
                }
            }
            """, "T.Test()");
        Assert.Equal("7|-1", result);
    }

    [Fact]
    public void CustomProperty_PropertyPattern_UsesGetter()
    {
        var result = TestHelper.TranspileAndRunWithRuntime(TempClass + """
            public class T
            {
                public static string Test()
                {
                    var t = new Temp();
                    t.Celsius = 25;
                    var hot = t is { Celsius: > 20 };
                    var cold = t is { Celsius: < 20 };
                    return $"{hot}|{cold}";
                }
            }
            """, "T.Test()");
        Assert.Equal("true|false", result);
    }

    [Fact]
    public void AutoProperty_StaysRawField()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            public class Mixed
            {
                public int Auto { get; set; }

                public int Twice => Auto * 2;
            }
            public class T
            {
                public static string Test()
                {
                    var m = new Mixed { Auto = 4 };
                    m.Auto += 1;
                    return $"{m.Auto}|{m.Twice}";
                }
            }
            """, "T.Test()");
        Assert.Equal("5|10", result);
    }
}
