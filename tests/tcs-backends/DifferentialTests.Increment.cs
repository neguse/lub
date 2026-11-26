namespace TinyCs.Tcs2c.Tests;

// Two-backend differential tests for ++ / -- in expression position and operand evaluation order
public class IncrementDifferentialTests
{
    // ++ / -- in expression position: prefix gives the value after the update, postfix the value before. Condition / argument / index /
    // assignment right side / return / field / custom property / receiver with side effects / Nullable
    [CFact]
    public void IncrementAsExpression_PrefixAndPostfixValues()
    {
        Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box
            {
                public int N;
                public int Sets;
                private int _v;
                public int V { get { return _v; } set { Sets++; _v = value; } }
            }
            public class P
            {
                public static int Counter;
                public static int Calls;
                public static Box B = new Box();
                public static Box Get() { Calls++; return B; }
                public static int Idx() { Calls++; return 1; }
                public static int Twice(int x) => x * 2;
                public static int Next() => Counter++;
                public static int Bump() => ++Counter;
                public static int F(int n) { int k = 0; for (int i = 0; i < n; i++) { if (++k > 3) break; } return k; }
                public static void Main()
                {
                    int i = 5;
                    int a = Twice(i++);
                    int[] buf = new int[3];
                    int pos = 0;
                    buf[pos++] = 7;
                    buf[pos++] = 8;
                    int j = 2;
                    int b = buf[--j];
                    int x = 1;
                    int y = x++ + 10;
                    int z = --x * 2;
                    Console.WriteLine(F(10) + "|" + a + "|" + i + "|" + pos + "|" + buf[0] + "," + buf[1] + "|" + b + "|" + j + "|" + x + "|" + y + "|" + z);
                    Console.WriteLine(Next() + "," + Bump() + "," + Next() + "|" + Counter);
                    var box = new Box();
                    box.N = 3;
                    int c = box.N++;
                    int d = --box.N;
                    box.V = 10;
                    int e = box.V++;
                    int f = ++box.V;
                    Console.WriteLine(c + "|" + d + "|" + box.N + "|" + e + "|" + f + "|" + box.V + "|" + box.Sets);
                    var data = new List<int> { 1, 2, 3 };
                    int g = Get().N++;
                    int h = ++Get().N;
                    int k1 = data[Idx()]++;
                    int k2 = --data[Idx()];
                    Console.WriteLine(g + "|" + h + "|" + B.N + "|" + k1 + "|" + k2 + "|" + data[1] + "|" + Calls);
                    int? n = 5;
                    int? none = null;
                    int? p1 = n++;
                    int? p2 = ++n;
                    int? p3 = none++;
                    Console.WriteLine(p1 + "|" + p2 + "|" + n + "|" + p3.HasValue + "|" + none.HasValue);
                    float fl = 1.5f;
                    float fo = fl++;
                    Console.WriteLine(fo + "|" + fl + "|" + (--fl));
                }
            }
            """, "P");
    }

    // Operands are evaluated from the left: even if the left operand / assignment target index reads a local
    // that a ++ / assignment on the right rewrites, it gets the value before the rewrite (C# results verified as fixed values)
    [CFact]
    public void EvaluationOrder_LeftOperandAndIndexBeforeRhsSideEffect()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Pt { public int F; }
            public class P
            {
                public static int SF = 1;
                public static int Add(int x, int y) => x * 10 + y;
                public static void Main()
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
                    var p = new Pt();
                    p.F = 1;
                    int h = p.F + p.F++;
                    int t = 1;
                    Func<int> bump = () => t++;
                    int tb = t + bump();
                    Console.WriteLine(a + "," + i + "|" + b + "," + j + "|" + q + "|" + c + "," + fz + "|" + d + "," + r + "|" + e + "|" + f + "|" + g + "|" + h + "|" + tb + "," + t);
                    var dict = new Dictionary<int, int>();
                    int k = 1;
                    dict[k] = k++;
                    int u = 1;
                    dict[u] += u++;
                    int k2 = 3;
                    int v = (dict[k2] = k2++);
                    var list = new List<int> { 0, 0, 0 };
                    int n = 0;
                    list[n] = n++ + 10;
                    int[] arr = new int[3];
                    int m = 0;
                    arr[m] = m++ + 20;
                    Console.WriteLine(dict.Count + "," + dict[1] + "," + dict[3] + "," + v + "|" + list[0] + "," + list[1] + "|" + arr[0] + "," + arr[1]);
                }
            }
            """, "P");
        Assert.Equal("2,2|3,2|2|1,1.5|6,5|3|11|2|2|2,2\n2,2,3,3|10,0|20,0",
            output.ReplaceLineEndings("\n"));
    }

    // A receiver obtained through a getter is evaluated once, and a custom property assignment in expression position
    // goes through the accessor (C# results verified as fixed values)
    [CFact]
    public void GetterReceiverAndCustomPropertyAssignAsExpression()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box
            {
                public int N;
                int _p = 4;
                public int P { get { return _p; } set { _p = value; } }
                string? _name;
                public string? Name { get { return _name; } set { _name = value; } }
            }
            public class P
            {
                static int Calls;
                static Box A = new Box { N = 10 };
                static Box B = new Box { N = 20 };
                static Box Current { get { Calls++; return Calls == 1 ? A : B; } }
                static Box Get() { return B; }
                public static void Main()
                {
                    int old = Current.N++;
                    Console.WriteLine(old + "|" + A.N + "|" + B.N + "|" + Calls);
                    int n = (Get().P += 1);
                    int sum = n + B.P;
                    int s = (Get().P = 3);
                    string? c1 = (Get().Name ??= "a");
                    string? c2 = (Get().Name ??= "b");
                    Console.WriteLine(sum + "|" + s + "|" + c1 + c2 + B.Name);
                }
            }
            """, "P");
        Assert.Equal("10|11|20|1\n10|3|aaa", output.ReplaceLineEndings("\n"));
    }

    // Place of the form getter → struct field → (struct field): evaluates only the reference-type prefix
    // once, and writes to the original field without copying the struct
    [CFact]
    public void GetterThroughStructField_EvaluatedOnceAndWritesInPlace()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct In { public int N; }
            public struct S
            {
                public int N;
                public In I;
                int _p;
                public int P { get { return _p; } set { _p = value; } }
            }
            public class Box { public S S; public S[] Arr = new S[2]; }
            public class P
            {
                static int Calls;
                static Box A = new Box();
                static Box B = new Box();
                static Box Current { get { Calls++; return Calls == 1 ? A : B; } }
                static Box Get() { Calls++; return A; }
                static void Reset() { Calls = 0; A = new Box(); B = new Box(); }
                static string Show() => A.S.N + "," + A.S.I.N + "," + A.S.P + "," + A.Arr[1].N + "|" + B.S.N + "," + B.S.I.N + "," + B.S.P + "," + B.Arr[1].N + "|" + Calls;
                public static void Main()
                {
                    Reset();
                    int old = Current.S.N++;
                    Console.WriteLine(old + ":" + Show());
                    Reset();
                    Current.S.N++;
                    Console.WriteLine(Show());
                    Reset();
                    Current.S.N += 5;
                    Console.WriteLine(Show());
                    Reset();
                    int v = (Current.S.N += 5);
                    Console.WriteLine(v + ":" + Show());
                    Reset();
                    int w = ++Current.S.I.N;
                    Current.S.I.N++;
                    Console.WriteLine(w + ":" + Show());
                    Reset();
                    int p = Current.S.P++;
                    Console.WriteLine(p + ":" + Show());
                    Reset();
                    int q = Get().S.N++;
                    Get().Arr[1].N += 3;
                    Console.WriteLine(q + ":" + Show());
                    Reset();
                    int u = Current.Arr[1].N++;
                    Console.WriteLine(u + ":" + Show());
                }
            }
            """, "P");
        Assert.Equal(
            "0:1,0,0,0|0,0,0,0|1\n1,0,0,0|0,0,0,0|1\n5,0,0,0|0,0,0,0|1\n"
            + "5:5,0,0,0|0,0,0,0|1\n1:0,1,0,0|0,1,0,0|2\n0:0,0,1,0|0,0,0,0|1\n"
            + "0:1,0,0,3|0,0,0,0|2\n0:0,0,0,1|0,0,0,0|1",
            output.ReplaceLineEndings("\n").TrimEnd());
    }

    // ++ / -- on a custom property in expression position writes to the receiver read first, even if the getter replaces the receiver's field (#418)
    [CFact]
    public void IncrementExpression_CustomPropertyReceiverReadOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box
            {
                public int Raw;
                public int Value
                {
                    get { P.Current = new Box { Raw = 100 }; return Raw; }
                    set { Raw = value; }
                }
            }
            public static class P
            {
                public static Box Current;
                public static void Main()
                {
                    Current = new Box { Raw = 10 };
                    Box old = Current;
                    Console.WriteLine(Current.Value++);
                    Console.WriteLine(old.Raw + "," + Current.Raw);
                    old = Current;
                    Console.WriteLine(--Current.Value);
                    Console.WriteLine(old.Raw + "," + Current.Raw);
                }
            }
            """, "P");
        Assert.Equal("10\n11,100\n99\n99,100", output);
    }
}
