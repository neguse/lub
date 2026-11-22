namespace TinyCs.Tests;

// ++ / -- in expression position "assign and return the value" (prefix returns the updated value, postfix the value before the update).
// This is a separate path from statement position (`i++;` / the update part of for), so this covers conditions / arguments / indexes /
// assignment right-hand sides / return / field / property / receivers with side effects / Nullable
public class IncrementExpressionTests
{
    [Fact]
    public void PrefixInCondition_UpdatesAndYieldsNewValue()
    {
        var result = TestHelper.CompileAndRunVm("""
            public class T
            {
                public static int Test(int n)
                {
                    int k = 0;
                    for (int i = 0; i < n; i++) { if (++k > 3) break; }
                    return k;
                }
            }
            """, "T.Test(10)");
        Assert.Equal("4", result);
    }

    [Fact]
    public void PostfixAsArgumentAndIndex_YieldsOldValue()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("10|6|2|7,8|8|1", result);
    }

    [Fact]
    public void InAssignmentRhsAndReturn_PrefixAndPostfixDiffer()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("1|11|2|0,2,2|3", result);
    }

    [Fact]
    public void FieldAndCustomProperty_AccessorsRunOnce()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("3|3|3|10|12|12|3|3", result);
    }

    [Fact]
    public void SideEffectReceiverAndIndex_EvaluatedOnce()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("10|12|12|2|2|2|4", result);
    }

    [Fact]
    public void NullableOperand_LiftedInExpression()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("5|7|7|0|0", result);
    }

    // C# evaluates the left operand first. Even if ++ / assignment in the right operand rewrites the same local,
    // the left uses the value before the rewrite and is not dragged along by Lua's deferred local reference
    [Fact]
    public void LocalLeftOperand_ReadBeforeRightSideEffect()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("2,2|3,2|2|1,1.5|6,5|3|11|2|-1,1", result);
    }

    // Index assignment evaluates the index before the right-hand side (Dictionary / List / array)
    [Fact]
    public void IndexedAssignment_IndexEvaluatedBeforeRhs()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("1,2|10,0|20,0|2", result);
    }

    [Fact]
    public void ExpressionIncrement_IsNotDiagnosed()
    {
        var result = Checker.Check(["""
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

    // A receiver reached through a getter is evaluated only once, and the object that was read is updated
    [Fact]
    public void GetterReceiver_EvaluatedOnce()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("10|11|20|1 11|20|1 15|20|1", result);
    }

    // Compound assignment / assignment in expression position also goes through the accessor for custom properties, and the value is
    // the assigned value (the getter is not read again)
    [Fact]
    public void CustomPropertyAssignAsExpression_UsesAccessors()
    {
        var result = TestHelper.CompileAndRunVm("""
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
            """, "T.Test()");
        Assert.Equal("5|7|9|9|2|4|aaa", result);
    }

    // For a getter → struct field → (struct field) place, only the reference-type prefix that has the getter
    // is fixed, and the struct is not copied but written into the original field
    [Fact]
    public void GetterThroughStructField_EvaluatedOnceAndWritesInPlace()
    {
        var result = TestHelper.CompileAndRunVm("""
            public struct In { public int N; }
            public struct S
            {
                public int N;
                public In I;
                int _p;
                public int P { get { return _p; } set { _p = value; } }
            }
            public class Box { public S S; public S[] Arr = new S[2]; }
            public class T
            {
                static int Calls;
                static Box A = new Box(), B = new Box();
                static Box Current { get { Calls++; return Calls == 1 ? A : B; } }
                static Box Get() { Calls++; return A; }
                static void Reset() { Calls = 0; A = new Box(); B = new Box(); }
                static string Show() => $"{A.S.N},{A.S.I.N},{A.S.P},{A.Arr[1].N}|{B.S.N},{B.S.I.N},{B.S.P},{B.Arr[1].N}|{Calls}";
                public static string Test()
                {
                    Reset();
                    int old = Current.S.N++;
                    string r1 = $"{old}:{Show()}";
                    Reset();
                    Current.S.N++;
                    string r2 = Show();
                    Reset();
                    Current.S.N += 5;
                    string r3 = Show();
                    Reset();
                    int v = (Current.S.N += 5);
                    string r4 = $"{v}:{Show()}";
                    Reset();
                    int w = ++Current.S.I.N;
                    Current.S.I.N++;
                    string r5 = $"{w}:{Show()}";
                    Reset();
                    int p = Current.S.P++;
                    string r6 = $"{p}:{Show()}";
                    Reset();
                    int q = Get().S.N++;
                    Get().Arr[1].N += 3;
                    string r7 = $"{q}:{Show()}";
                    Reset();
                    int u = Current.Arr[1].N++;
                    string r8 = $"{u}:{Show()}";
                    return $"{r1} {r2} {r3} {r4} {r5} {r6} {r7} {r8}";
                }
            }
            """, "T.Test()");
        Assert.Equal(
            "0:1,0,0,0|0,0,0,0|1 1,0,0,0|0,0,0,0|1 5,0,0,0|0,0,0,0|1 "
            + "5:5,0,0,0|0,0,0,0|1 1:0,1,0,0|0,1,0,0|2 0:0,0,1,0|0,0,0,0|1 "
            + "0:1,0,0,3|0,0,0,0|2 0:0,0,0,1|0,0,0,0|1", result);
    }
}
