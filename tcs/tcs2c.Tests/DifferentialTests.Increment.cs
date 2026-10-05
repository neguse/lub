namespace TinyCs.Tcs2c.Tests;

// 式位置の ++ / -- と operand 評価順の 2 backend differential
public partial class DifferentialTests
{
    // 式位置の ++ / --: 前置は更新後、後置は更新前の値。条件 / 引数 / 添字 /
    // 代入右辺 / return / field / custom property / 副作用 receiver / Nullable
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

    // operand は左から評価: 右側の ++ / 代入が書き換える local を左の
    // operand / 代入先の添字が読んでも書き換え前の値 (C# の結果を固定値で検証)
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

    // getter 経由の receiver は 1 回評価、式位置の custom property 代入は
    // accessor を通す (C# の結果を固定値で検証)
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
}

