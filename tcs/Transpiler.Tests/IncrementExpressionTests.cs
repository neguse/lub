namespace TinyCs.Tests;

// 式位置の ++ / -- は「代入しつつ値を返す」(前置は更新後、後置は更新前)。
// 文位置 (`i++;` / for の更新部) とは別経路なので、条件 / 引数 / 添字 /
// 代入右辺 / return / field / property / 副作用 receiver / Nullable を網羅する
public class IncrementExpressionTests
{
    [Fact]
    public void PrefixInCondition_UpdatesAndYieldsNewValue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Test(int n)
                {
                    int k = 0;
                    for (int i = 0; i < n; i++) { if (++k > 3) break; }
                    return k;
                }
            }
            """, "T.test(10)");
        Assert.Equal("4", result);
    }

    [Fact]
    public void PostfixAsArgumentAndIndex_YieldsOldValue()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Twice(int x) => x * 2;

                public static string Test()
                {
                    int i = 5;
                    int a = Twice(i++);
                    int[] buf = new int[3];
                    int pos = 0;
                    buf[pos++] = 7;
                    buf[pos++] = 8;
                    int j = 2;
                    int b = buf[--j];
                    return $"{a}|{i}|{pos}|{buf[0]},{buf[1]}|{b}|{j}";
                }
            }
            """, "T.test()");
        Assert.Equal("10|6|2|7,8|8|1", result);
    }

    [Fact]
    public void InAssignmentRhsAndReturn_PrefixAndPostfixDiffer()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int Counter;

                public static int Next() => Counter++;
                public static int Bump() => ++Counter;

                public static string Test()
                {
                    int x = 1;
                    int y = x++ + 10;
                    int z = --x * 2;
                    int n1 = Next();
                    int n2 = Bump();
                    int n3 = Next();
                    return $"{x}|{y}|{z}|{n1},{n2},{n3}|{Counter}";
                }
            }
            """, "T.test()");
        Assert.Equal("1|11|2|0,2,2|3", result);
    }

    [Fact]
    public void FieldAndCustomProperty_AccessorsRunOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                public int N;
                public int Gets;
                public int Sets;
                private int _v;
                public int V
                {
                    get { Gets++; return _v; }
                    set { Sets++; _v = value; }
                }
            }

            public class T
            {
                public static string Test()
                {
                    var b = new Box();
                    b.N = 3;
                    int a = b.N++;
                    int c = --b.N;
                    b.V = 10;
                    int d = b.V++;
                    int e = ++b.V;
                    return $"{a}|{c}|{b.N}|{d}|{e}|{b.V}|{b.Gets}|{b.Sets}";
                }
            }
            """, "T.test()");
        Assert.Equal("3|3|3|10|12|12|3|3", result);
    }

    [Fact]
    public void SideEffectReceiverAndIndex_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            using System.Collections.Generic;

            public class Box { public int X = 10; }

            public class T
            {
                public static int Calls;
                public static Box B = new Box();
                public static List<int> Data = new List<int> { 1, 2, 3 };

                public static Box Get() { Calls++; return B; }
                public static int Idx() { Calls++; return 1; }

                public static string Test()
                {
                    int a = Get().X++;
                    int b = ++Get().X;
                    int c = Data[Idx()]++;
                    int d = --Data[Idx()];
                    return $"{a}|{b}|{B.X}|{c}|{d}|{Data[1]}|{Calls}";
                }
            }
            """, "T.test()");
        Assert.Equal("10|12|12|2|2|2|4", result);
    }

    [Fact]
    public void NullableOperand_LiftedInExpression()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static string Test()
                {
                    int? n = 5;
                    int? none = null;
                    int? a = n++;
                    int? b = ++n;
                    int? c = none++;
                    return $"{a}|{b}|{n}|{(c.HasValue ? 1 : 0)}|{(none.HasValue ? 1 : 0)}";
                }
            }
            """, "T.test()");
        Assert.Equal("5|7|7|0|0", result);
    }

    [Fact]
    public void ExpressionIncrement_IsNotDiagnosed()
    {
        var result = Transpiler.TranspileWithDiagnostics(["""
            public class T
            {
                public static void F(int x) { }

                public static int Test()
                {
                    int i = 0;
                    F(i++);
                    return ++i;
                }
            }
            """]);

        Assert.True(result.Success);
        Assert.Empty(result.Warnings);
    }
}
