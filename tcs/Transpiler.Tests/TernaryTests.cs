namespace TinyCs.Tests;

public class TernaryTests
{
    [Fact]
    public void Ternary_TrueBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Max(int a, int b)
                {
                    return a > b ? a : b;
                }
            }
            """,
            "T.max(10, 5)");
        Assert.Equal("10", result);
    }

    [Fact]
    public void Ternary_FalseBranch()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Max(int a, int b)
                {
                    return a > b ? a : b;
                }
            }
            """,
            "T.max(3, 8)");
        Assert.Equal("8", result);
    }

    [Fact]
    public void Ternary_FalsyTrueValue()
    {
        // true 側が 0 (Lua では falsy) でも `and/or` に落とさず if 文で選ぶ
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Pick(bool cond)
                {
                    return cond ? 0 : 99;
                }
            }
            """,
            "T.pick(true)");
        Assert.Equal("0", result);
    }

    // ---- 式位置の条件式は temp local + if 文に下ろす (#25)。値意味論は
    // TranspileAndRun (dotnet differential 込み)、出力形は IIFE の有無だけ検査 ----

    private const string Iife = "end)()";

    private static string Run(string cs, string luaExpr) =>
        TestHelper.TranspileAndRunWithRuntime(cs, luaExpr);

    [Fact]
    public void Ternary_ParenthesizedReturn_NoClosure()
    {
        var cs = """
            public class T
            {
                public static int Pick(bool c, int a, int b) { return (c ? a : b); }
            }
            """;
        Assert.Equal("5", Run(cs, "T.pick(false, 3, 5)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_MidExpression_NoClosure()
    {
        var cs = """
            public class T
            {
                public static int Mid(bool c, int a) { return a + (c ? 1 : 2) * 3; }
                public static int Two(bool c, bool d) { return (c ? 1 : 2) * 10 + (d ? 3 : 4); }
            }
            """;
        Assert.Equal("16", Run(cs, "T.mid(false, 10)"));
        Assert.Equal("13", Run(cs, "T.two(true, true)"));
        Assert.Equal("24", Run(cs, "T.two(false, false)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_Nested_NoClosure()
    {
        var cs = """
            public class T
            {
                public static int Sign(int x) { return x < 0 ? -1 : (x == 0 ? 0 : 1); }
                public static int CondIsTernary(bool c, bool d, bool e) { return (c ? d : e) ? 1 : 2; }
                public static int InLocal(int x)
                {
                    int r = x > 10 ? (x > 20 ? 3 : 2) : 1;
                    return r;
                }
            }
            """;
        Assert.Equal("-1", Run(cs, "T.sign(-5)"));
        Assert.Equal("0", Run(cs, "T.sign(0)"));
        Assert.Equal("1", Run(cs, "T.sign(7)"));
        Assert.Equal("2", Run(cs, "T.cond_is_ternary(false, true, false)"));
        Assert.Equal("1", Run(cs, "T.cond_is_ternary(true, true, false)"));
        Assert.Equal("3", Run(cs, "T.in_local(25)"));
        Assert.Equal("2", Run(cs, "T.in_local(15)"));
        Assert.Equal("1", Run(cs, "T.in_local(5)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_InArgumentsAndCallChain()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                static int Add(int a, int b) { return a + b; }
                public static int Args(bool c) { return Add(c ? 1 : 2, c ? 30 : 40); }
                public static int Receiver(bool c)
                {
                    var l1 = new List<int> { 7 };
                    var l2 = new List<int> { 9 };
                    return (c ? l1 : l2)[0] + (c ? l1 : l2).Count;
                }
                public static string Interp(bool c) { return $"<{(c ? "yes" : "no")}>"; }
            }
            """;
        Assert.Equal("31", Run(cs, "T.args(true)"));
        Assert.Equal("42", Run(cs, "T.args(false)"));
        Assert.Equal("10", Run(cs, "T.receiver(false)"));
        Assert.Equal("<yes>", Run(cs, "T.interp(true)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_EvaluationOrder_LeftOperandBeforeCondition()
    {
        // C# は左 operand を条件より先に評価する。条件や分岐が左 operand を
        // 書き換えても、読み取り済みの値が使われる
        var cs = """
            public class T
            {
                static int x;
                static string log = "";
                static bool SetX(int v) { x = v; log += "c"; return true; }
                static int Val(int v) { log += "v"; return v; }
                static int Read() { log += "r"; return x; }
                public static int FieldReadBeforeCond()
                {
                    x = 1;
                    return x + (SetX(10) ? 1 : 2);
                }
                public static int FieldReadBeforeBranch()
                {
                    x = 1;
                    return x + (true ? SetXAnd(5) : 0);
                }
                static int SetXAnd(int v) { x = 100; return v; }
                public static string CallOrder()
                {
                    log = "";
                    int r = Read() + (SetX(2) ? Val(3) : Val(4)) + Read();
                    return log + r;
                }
                public static string IndexOrder()
                {
                    log = "";
                    var arr = new int[3];
                    arr[Val(1)] = SetX(2) ? Val(5) : 0;
                    return log + arr[1];
                }
            }
            """;
        Assert.Equal("2", Run(cs, "T.field_read_before_cond()"));
        Assert.Equal("6", Run(cs, "T.field_read_before_branch()"));
        Assert.Equal("rcvr5", Run(cs, "T.call_order()"));
        Assert.Equal("vcv5", Run(cs, "T.index_order()"));
    }

    [Fact]
    public void Ternary_ShortCircuitRightOperand_NotEvaluatedEagerly()
    {
        var cs = """
            using System.Collections.Generic;
            public class T
            {
                public static bool And(List<int> l) { return l != null && (l[0] > 0 ? true : false); }
                public static bool Or(List<int> l) { return l == null || (l[0] > 0 ? false : true); }
                public static int Chain(string s, bool c)
                {
                    bool b = s != null && s.Length > 0 && (c ? s[0] == 'a' : s[0] == 'b');
                    return b ? 1 : 0;
                }
            }
            """;
        Assert.Equal("false", Run(cs, "T.and_(nil)"));
        Assert.Equal("true", Run(cs, "T.and_(List.new({5}))"));
        Assert.Equal("true", Run(cs, "T.or_(nil)"));
        Assert.Equal("false", Run(cs, "T.or_(List.new({5}))"));
        Assert.Equal("1", Run(cs, "T.chain(\"abc\", true)"));
        Assert.Equal("0", Run(cs, "T.chain(\"abc\", false)"));
        Assert.Equal("0", Run(cs, "T.chain(nil, true)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_InLoopConditions()
    {
        var cs = """
            public class T
            {
                public static int While(bool c)
                {
                    int i = 0, j = 0, n = 0;
                    while ((c ? i : j) < 5) { i++; j += 2; n++; }
                    return n;
                }
                public static int ForWithContinue(bool flag)
                {
                    int sum = 0;
                    for (int i = 0; i < (flag ? 10 : 5); i++)
                    {
                        if (i % 2 == 0) continue;
                        int odd = i;
                        sum += odd;
                    }
                    return sum;
                }
                public static int DoWhile(bool c)
                {
                    int i = 0;
                    do { i++; if (i == 2) continue; i++; } while (i < (c ? 10 : 5));
                    return i;
                }
                public static int ForeachColl(bool c)
                {
                    var a = new System.Collections.Generic.List<int> { 1, 2 };
                    var b = new System.Collections.Generic.List<int> { 10, 20, 30 };
                    int s = 0;
                    foreach (var v in c ? a : b) s += v;
                    return s;
                }
            }
            """;
        Assert.Equal("5", Run(cs, "T.while_(true)"));
        Assert.Equal("3", Run(cs, "T.while_(false)"));
        Assert.Equal("25", Run(cs, "T.for_with_continue(true)"));
        Assert.Equal("4", Run(cs, "T.for_with_continue(false)"));
        Assert.Equal("10", Run(cs, "T.do_while(true)"));
        Assert.Equal("6", Run(cs, "T.do_while(false)"));
        Assert.Equal("60", Run(cs, "T.foreach_coll(false)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_InIfConditions()
    {
        var cs = """
            public class T
            {
                public static int Test(int x, bool c)
                {
                    if ((c ? x : -x) > 3) return 1;
                    else if ((c ? -x : x) > 3) return 2;
                    else if (x == 0) return 3;
                    return 4;
                }
            }
            """;
        Assert.Equal("1", Run(cs, "T.test(5, true)"));
        Assert.Equal("2", Run(cs, "T.test(5, false)"));
        Assert.Equal("3", Run(cs, "T.test(0, true)"));
        Assert.Equal("4", Run(cs, "T.test(1, true)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_InLambdaBodies()
    {
        var cs = """
            using System;
            using System.Collections.Generic;
            public class T
            {
                public static int Test(bool c)
                {
                    Func<int, int> f = x => x + (x > 2 ? 10 : 20);
                    var list = new List<int>();
                    Action<int> push = v => list.Add(c ? v : -v);
                    push(1);
                    push(2);
                    Func<int> g = () => { return (c ? f(1) : f(5)) + list[0]; };
                    return g() + list[1];
                }
                public static int Outer(bool c)
                {
                    Func<Func<int>, int> call = h => h();
                    return c ? call(() => c ? 1 : 2) : 3;
                }
            }
            """;
        Assert.Equal("24", Run(cs, "T.test(true)"));
        Assert.Equal("12", Run(cs, "T.test(false)"));
        Assert.Equal("1", Run(cs, "T.outer(true)"));
        Assert.Equal("3", Run(cs, "T.outer(false)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_InFieldInitializerAndCompoundAssignment()
    {
        var cs = """
            public class T
            {
                static bool Flag = true;
                int x = Flag ? 1 : 2;
                public int P { get; set; }
                int store;
                public int Custom { get => store; set => store = value * 2; }
                public static int Test(bool c)
                {
                    var t = new T();
                    t.x += c ? 10 : 20;
                    t.P += c ? 100 : 200;
                    t.Custom += c ? 1 : 2;
                    string s = "a";
                    s += c ? "b" : "c";
                    return t.x + t.P + t.Custom + s.Length;
                }
            }
            """;
        Assert.Equal("115", Run(cs, "T.test(true)"));
        Assert.Equal("227", Run(cs, "T.test(false)"));
    }

    [Fact]
    public void Ternary_WithNullConditionalAndCoalesce()
    {
        var cs = """
            public class T
            {
                public static int Test(string s, bool c)
                {
                    int v = (s?.Length ?? 0) + (c ? 1 : 2);
                    string r = (c ? s : null) ?? "dflt";
                    int? n = c ? 1 : null;
                    return v * 100 + r.Length * 10 + (n ?? 7);
                }
            }
            """;
        Assert.Equal("431", Run(cs, "T.test(\"abc\", true)"));
        Assert.Equal("247", Run(cs, "T.test(nil, false)"));
        Assert.Equal("547", Run(cs, "T.test(\"abc\", false)"));
    }

    [Fact]
    public void Ternary_InSwitchExpressionAndObjectInitializer()
    {
        var cs = """
            public class P { public int X; public int Y; }
            public class T
            {
                public static int Test(int k, bool c)
                {
                    int v = k switch { 1 => c ? 10 : 20, 2 => 30, _ => c ? 0 : -1 };
                    var p = new P { X = c ? 1 : 2, Y = v };
                    return p.X + p.Y;
                }
            }
            """;
        Assert.Equal("11", Run(cs, "T.test(1, true)"));
        Assert.Equal("22", Run(cs, "T.test(1, false)"));
        Assert.Equal("1", Run(cs, "T.test(9, false)"));
    }

    [Fact]
    public void Ternary_StructCopyOfSelectedValue()
    {
        var cs = """
            public struct S { public int V; }
            public class T
            {
                public static int Test(bool c)
                {
                    S a = new S { V = 1 };
                    S b = new S { V = 2 };
                    S s = c ? a : b;
                    s.V = 99;
                    return a.V * 10 + b.V;
                }
            }
            """;
        Assert.Equal("12", Run(cs, "T.test(true)"));
        Assert.Equal("12", Run(cs, "T.test(false)"));
    }

    [Fact]
    public void Ternary_ManyInOneBody_TempsStayScoped()
    {
        // Lua の local 上限 (200) を temp で踏まないこと
        var body = string.Join("\n", Enumerable.Range(0, 230)
            .Select(i => $"        sum += (i % 2 == 0 ? {i} : 1) * 2;"));
        var cs = $$"""
            public class T
            {
                public static int Test(int i)
                {
                    int sum = 0;
            {{body}}
                    return sum;
                }
            }
            """;
        Assert.Equal("460", Run(cs, "T.test(1)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Coalesce_CallDefault_LazyAndNoClosure()
    {
        // `??` の右辺が呼び出しを含むときも IIFE ではなく if 文で、nil のときだけ評価する
        var cs = """
            public class T
            {
                static string log = "";
                static string Make(string s) { log += "m"; return s; }
                public static string Test(string a, string b, bool c)
                {
                    log = "";
                    string r = (a ?? Make("x")) + (b ?? (c ? Make("y") : Make("z")));
                    return log + r;
                }
            }
            """;
        Assert.Equal("1a2", Run(cs, "T.test(\"1a\", \"2\", true)"));
        Assert.Equal("mx2", Run(cs, "T.test(nil, \"2\", true)"));
        Assert.Equal("mmxy", Run(cs, "T.test(nil, nil, true)"));
        Assert.Equal("mmxz", Run(cs, "T.test(nil, nil, false)"));
        Assert.DoesNotContain(Iife, Transpiler.Transpile(cs));
    }

    [Fact]
    public void Ternary_EvaluationOrder_LeftOperandBeforeLaterSideEffect()
    {
        // 三項より左の兄弟 operand は、その間にある呼び出しの副作用より先に読む
        var cs = """
            using System;
            public class B { public int V; }
            public class T
            {
                static B b = new B();
                static int Set() { b.V = 100; return 0; }
                static int Sum3(int x, int y, int z) { return x + y + z; }
                public static int Args(bool c)
                {
                    b.V = 1;
                    return Sum3(b.V, Set(), c ? 1 : 2);
                }
                public static int ArrayInit(bool c)
                {
                    b.V = 1;
                    int[] a = new int[] { b.V, Set(), c ? 1 : 2 };
                    return a[0];
                }
                public static int CapturedLocal(bool c)
                {
                    int loc = 1;
                    Func<int> g = () => { loc = 50; return 0; };
                    return Sum3(loc, g(), c ? 1 : 2);
                }
            }
            """;
        Assert.Equal("2", Run(cs, "T.args(true)"));
        Assert.Equal("1", Run(cs, "T.array_init(true)"));
        Assert.Equal("2", Run(cs, "T.captured_local(true)"));
    }

    [Fact]
    public void Coalesce_TernaryDefault_NotEvaluatedWhenLeftHasValue()
    {
        var cs = """
            public class N { public int F; }
            public class T
            {
                public static int Test(N n)
                {
                    int? x = 5;
                    int r = x ?? (n!.F > 0 ? 1 : 2);
                    return r;
                }
            }
            """;
        Assert.Equal("5", Run(cs, "T.test(nil)"));
    }

    [Fact]
    public void Ternary_AssignToSelfFieldChain_ReceiverFixedBeforeCondition()
    {
        // self.Current.V の受け手 self.Current は条件 (Swap()) より先に評価する
        var cs = """
            public class Box { public int V; }
            public class T
            {
                public Box Current = new Box();
                bool Swap() { Current = new Box(); return true; }
                public int Test()
                {
                    var old = Current;
                    Current.V = Swap() ? 7 : 9;
                    return old.V * 10 + Current.V;
                }
            }
            """;
        Assert.Equal("70", Run(cs, "T.new():test()"));
    }

    [Fact]
    public void Ternary_UserOperatorInCondition_LeftOperandReadFirst()
    {
        // a + b はユーザー定義演算子 (metamethod) を呼ぶので、左の X を先に読む
        var cs = """
            public class C
            {
                public static int operator +(C a, C b) { T.X = 9; return 1; }
            }
            public class T
            {
                public static int X;
                public static int Test()
                {
                    X = 1;
                    var a = new C(); var b = new C();
                    return X + ((a + b > 0) ? 1 : 2);
                }
            }
            """;
        Assert.Equal("2", Run(cs, "T.test()"));
    }
}
