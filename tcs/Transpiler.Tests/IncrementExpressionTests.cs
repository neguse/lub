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

    // C# は左オペランドを先に評価する。右オペランドの ++ / 代入が同じ local を
    // 書き換えても左は書き換え前の値で、Lua の local 参照遅延に引きずられない
    [Fact]
    public void LocalLeftOperand_ReadBeforeRightSideEffect()
    {
        var result = TestHelper.TranspileAndRun("""
            public class T
            {
                public static int SF = 1;
                public static int Add(int x, int y) => x * 10 + y;

                public static string Test()
                {
                    int i = 1;
                    int a = i + i++;
                    int j = 1;
                    int b = j + ++j;
                    int q = 1;
                    q += q++;
                    float fz = 0.5f;
                    float c = fz + fz++;
                    int r = 1;
                    int d = r + (r = 5);
                    int w = 1;
                    int e = w++ + w;
                    int z = 1;
                    int f = Add(z, z++);
                    int g = SF + SF++;
                    int m = 2;
                    int h = m * (m-- - 3) + m;
                    return $"{a},{i}|{b},{j}|{q}|{c},{fz}|{d},{r}|{e}|{f}|{g}|{h},{m}";
                }
            }
            """, "T.test()");
        Assert.Equal("2,2|3,2|2|1,1.5|6,5|3|11|2|-1,1", result);
    }

    // 添字代入は index を右辺より先に評価する (Dictionary / List / 配列)
    [Fact]
    public void IndexedAssignment_IndexEvaluatedBeforeRhs()
    {
        var result = TestHelper.TranspileAndRunWithRuntime("""
            using System.Collections.Generic;

            public class T
            {
                public static string Test()
                {
                    var d = new Dictionary<int, int>();
                    int k = 1;
                    d[k] = k++;
                    var l = new List<int> { 0, 0, 0 };
                    int n = 0;
                    l[n] = n++ + 10;
                    int[] a = new int[3];
                    int m = 0;
                    a[m] = m++ + 20;
                    int u = 1;
                    d[u] += u++;
                    return $"{d.Count},{d[1]}|{l[0]},{l[1]}|{a[0]},{a[1]}|{u}";
                }
            }
            """, "T.test()");
        Assert.Equal("1,2|10,0|20,0|2", result);
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

    // getter 経由の receiver は 1 回だけ評価し、読んだ object を更新する
    [Fact]
    public void GetterReceiver_EvaluatedOnce()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box { public int N; }
            public class T
            {
                static int Calls;
                static Box A = new Box { N = 10 }, B = new Box { N = 20 };
                static Box Current { get { Calls++; return Calls == 1 ? A : B; } }
                public static string Test()
                {
                    int old = Current.N++;
                    string expr = $"{old}|{A.N}|{B.N}|{Calls}";
                    Calls = 0; A.N = 10; B.N = 20;
                    Current.N++;
                    string stat = $"{A.N}|{B.N}|{Calls}";
                    Calls = 0; A.N = 10; B.N = 20;
                    Current.N += 5;
                    return $"{expr} {stat} {A.N}|{B.N}|{Calls}";
                }
            }
            """, "T.test()");
        Assert.Equal("10|11|20|1 11|20|1 15|20|1", result);
    }

    // 式位置の複合代入 / 代入も custom property は accessor を通し、値は
    // 代入した値 (getter を読み直さない)
    [Fact]
    public void CustomPropertyAssignAsExpression_UsesAccessors()
    {
        var result = TestHelper.TranspileAndRun("""
            public class Box
            {
                int _p = 4;
                public int Gets;
                public int P { get { Gets++; return _p; } set { _p = value; } }
                string? _name;
                public string? Name { get { return _name; } set { _name = value; } }
            }
            public class T
            {
                static Box B = new Box();
                static int Calls;
                static Box Get() { Calls++; return B; }
                public static string Test()
                {
                    int n = (Get().P += 1);
                    int m = (B.P += 2);
                    int s = (Get().P = 9);
                    int gets = B.Gets;
                    string? c1 = (Get().Name ??= "a");
                    string? c2 = (Get().Name ??= "b");
                    return $"{n}|{m}|{s}|{B.P}|{gets}|{Calls}|{c1}{c2}{B.Name}";
                }
            }
            """, "T.test()");
        Assert.Equal("5|7|9|9|2|4|aaa", result);
    }
}
