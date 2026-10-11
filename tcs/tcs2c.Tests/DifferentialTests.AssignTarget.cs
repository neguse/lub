namespace TinyCs.Tcs2c.Tests;

// 代入先 (受け手 / 添字 / struct の格納先) は右辺より先に 1 回だけ評価し、
// 式位置の代入の値は代入した値 (左辺を組み直して読まない)。期待値は同じ
// program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    // 右辺が受け手を差し替えても、式の値は代入した値で受け手は 1 回だけ評価
    [CFact]
    public void AssignmentExpression_ValueIsAssignedValue_ReceiverOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box { public int Value; public string? Name; }
            public class P
            {
                static int Calls;
                static Dictionary<int, int> D = new Dictionary<int, int>();
                static Box B = new Box();
                static List<int> L = new List<int> { 10 };
                static int[] A = new int[] { 10 };
                static Dictionary<int, int> RecvD() { Calls++; return D; }
                static Box RecvB() { Calls++; return B; }
                static List<int> RecvL() { Calls++; return L; }
                static int[] RecvA() { Calls++; return A; }
                static int Right()
                {
                    D = new Dictionary<int, int>(); D[1] = 100;
                    B = new Box(); B.Value = 100;
                    L = new List<int> { 100 };
                    A = new int[] { 100 };
                    return 7;
                }
                static string Name() { Right(); B.Name = "new"; return "set"; }
                static void Reset() { Calls = 0; D = new Dictionary<int, int>(); D[1] = 10; B = new Box(); B.Value = 10; L = new List<int> { 10 }; A = new int[] { 10 }; }
                public static void Main()
                {
                    Reset(); var od = D; int r1 = (RecvD()[1] = Right());
                    Console.WriteLine("dict:" + r1 + "," + Calls + "," + od[1] + "," + D[1]);
                    Reset(); var ob = B; int r2 = (RecvB().Value = Right());
                    Console.WriteLine("field:" + r2 + "," + Calls + "," + ob.Value + "," + B.Value);
                    Reset(); var ol = L; int r3 = (RecvL()[0] = Right());
                    Console.WriteLine("list:" + r3 + "," + Calls + "," + ol[0] + "," + L[0]);
                    Reset(); var oa = A; int r4 = (RecvA()[0] = Right());
                    Console.WriteLine("array:" + r4 + "," + Calls + "," + oa[0] + "," + A[0]);
                    Reset(); od = D; int r5 = (D[1] = Right());
                    Console.WriteLine("static-dict:" + r5 + "," + od[1] + "," + D[1]);
                    Reset(); ob = B; int r6 = (B.Value += Right());
                    Console.WriteLine("static-field+=:" + r6 + "," + ob.Value + "," + B.Value);
                    Reset(); oa = A; int r7 = (RecvA()[0] += Right());
                    Console.WriteLine("array+=:" + r7 + "," + Calls + "," + oa[0] + "," + A[0]);
                    Reset(); var box = new Box(); int r8 = (box.Value = Right()) + (box.Value += 1);
                    Console.WriteLine("local:" + r8 + "," + box.Value);
                    Reset(); ob = B; string? r9 = (RecvB().Name ??= Name());
                    Console.WriteLine("coalesce:" + r9 + "," + Calls + "," + ob.Name + "," + B.Name);
                }
            }
            """, "P");
        Assert.Equal("""
            dict:7,1,7,100
            field:7,1,7,100
            list:7,1,7,100
            array:7,1,7,100
            static-dict:7,7,100
            static-field+=:17,17,100
            array+=:17,1,17,100
            local:15,8
            coalesce:set,1,set,new
            """.ReplaceLineEndings("\n"), output);
    }

    // 式位置の custom property 代入の受け手、collection の method が変える
    // 添字 (`A[L.Count] = L.Remove(5) ? ..`) も右辺より先に固定する
    [CFact]
    public void AssignmentExpression_PropertyReceiverAndCollectionIndex_FixedBeforeRhs()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public class Box
            {
                int _p;
                public int Prop { get { P.Log += "g"; return _p; } set { P.Log += "s"; _p = value; } }
                public int Raw() => _p;
            }
            public struct S { public int N; }
            public class Holder { public S[] Arr = new S[4]; }
            public class P
            {
                public static string Log = "";
                static Box B = new Box();
                static int[] A = new int[4];
                static List<int> L = new List<int>();
                static Dictionary<int, int> D = new Dictionary<int, int>();
                static Holder H = new Holder();
                static int Right() { Log += "R"; B = new Box(); return 7; }
                static void Reset() { Log = ""; B = new Box(); A = new int[4]; L = new List<int> { 1, 5, 9 }; D = new Dictionary<int, int> { { 1, 1 }, { 2, 2 } }; H = new Holder(); }
                public static void Main()
                {
                    Reset(); var o = B; int r1 = (B.Prop = Right());
                    Console.WriteLine("prop=:" + r1 + "," + Log + "," + o.Raw() + "," + B.Raw());
                    Reset(); o = B; int r2 = (B.Prop += Right());
                    Console.WriteLine("prop+=:" + r2 + "," + Log + "," + o.Raw() + "," + B.Raw());
                    Reset(); int r3 = (A[L.Count] = L.Remove(5) ? 70 : 80);
                    Console.WriteLine("list-index:" + r3 + "," + A[3] + "," + A[2]);
                    Reset(); int r4 = (A[D.Count] = D.Remove(1) ? 90 : 0);
                    Console.WriteLine("dict-index:" + r4 + "," + A[2] + "," + A[1]);
                    Reset(); int r5 = (H.Arr[L.Count].N = L.Remove(5) ? 70 : 80);
                    Console.WriteLine("struct-index:" + r5 + "," + H.Arr[3].N + "," + H.Arr[2].N);
                    Reset(); H.Arr[L.Count].N += L.Remove(5) ? 70 : 80;
                    Console.WriteLine("struct-index-stmt:" + H.Arr[3].N + "," + H.Arr[2].N);
                }
            }
            """, "P");
        Assert.Equal("""
            prop=:7,Rs,7,0
            prop+=:7,gRs,7,0
            list-index:70,70,0
            dict-index:90,90,0
            struct-index:70,70,0
            struct-index-stmt:70,0
            """.ReplaceLineEndings("\n"), output);
    }

    // 右辺が struct の格納先 (class の field / 配列要素 / static field / this の
    // field / 捕捉 local の field) を差し替えても、書き込みは今の格納先に届く
    [CFact]
    public void StructFieldAssign_RhsReplacesStorage_WritesCurrentStorage()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct In { public int N; }
            public struct S { public int N; public int Other; public In I; public int Auto { get; set; } }
            public class Holder { public S Data; public S[] Arr = new S[1]; }
            public class P
            {
                static Holder H = new Holder();
                static S Shared;
                S mine;
                static int Replace()
                {
                    H.Data = new S { N = 100, Other = 8, Auto = 100 };
                    H.Data.I.N = 100;
                    H.Arr[0] = new S { N = 100, Other = 8 };
                    Shared = new S { N = 100, Other = 8 };
                    return 7;
                }
                static void Reset() { H = new Holder(); H.Data.N = 10; H.Data.Other = 2; H.Data.I.N = 10; H.Data.Auto = 10; H.Arr[0].N = 10; Shared = new S { N = 10, Other = 2 }; }
                static string Show(S s) => s.N + "," + s.Other;
                int ReplaceMine() { mine = new S { N = 100, Other = 8 }; return 7; }
                string Instance()
                {
                    mine.N = 10;
                    mine.N += ReplaceMine();
                    return Show(mine);
                }
                public static void Main()
                {
                    Reset(); int r = (H.Data.N += Replace());
                    Console.WriteLine("expr+=:" + r + ":" + Show(H.Data));
                    Reset(); H.Data.N += Replace();
                    Console.WriteLine("stmt+=:" + Show(H.Data));
                    Reset(); H.Data.N = Replace();
                    Console.WriteLine("stmt=:" + Show(H.Data));
                    Reset(); int e = (H.Data.N = Replace());
                    Console.WriteLine("expr=:" + e + ":" + Show(H.Data));
                    Reset(); H.Data.I.N += Replace();
                    Console.WriteLine("nested:" + H.Data.I.N + "," + H.Data.Other);
                    Reset(); H.Data.Auto += Replace();
                    Console.WriteLine("auto:" + H.Data.Auto + "," + H.Data.Other);
                    Reset(); H.Arr[0].N += Replace();
                    Console.WriteLine("array:" + Show(H.Arr[0]));
                    Reset(); Shared.N += Replace();
                    Console.WriteLine("static:" + Show(Shared));
                    Console.WriteLine("this:" + new P().Instance());
                    S loc = new S(); loc.I.N = 10;
                    Func<int> swap = () => { loc.I = new In { N = 100 }; return 7; };
                    loc.I.N += swap();
                    Console.WriteLine("captured:" + loc.I.N);
                    Reset(); var old = H; H.Data.N += ReplaceHolder();
                    Console.WriteLine("base:" + Show(old.Data) + "|" + Show(H.Data));
                    Reset(); H.Data.N += Math.Max(1, 2);
                    Console.WriteLine("pure:" + Show(H.Data));
                }
                static int ReplaceHolder() { H = new Holder(); H.Data.N = 100; return 7; }
            }
            """, "P");
        Assert.Equal("""
            expr+=:17:17,8
            stmt+=:17,8
            stmt=:7,8
            expr=:7:7,8
            nested:17,8
            auto:17,8
            array:17,8
            static:17,8
            this:17,8
            captured:17
            base:17,2|100,0
            pure:12,2
            """.ReplaceLineEndings("\n"), output);
    }

    // struct の custom property / ??= も今の格納先に書く
    [CFact]
    public void StructMemberAssign_PropertyAndCoalesce_WriteCurrentStorage()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; public string? Name; int _p; public int P { get { return _p; } set { _p = value; } } }
            public class Holder { public S Data; }
            public class Q
            {
                public static Holder H = new Holder();
                public static int Right() { H.Data = new S { N = 100 }; H.Data.P = 100; H.Data.Name = "new"; return 7; }
                public static string Name() { Right(); return "set"; }
            }
            public class P
            {
                public static void Main()
                {
                    Q.H = new Holder(); Q.H.Data.P = 10;
                    Q.H.Data.P += Q.Right();
                    Console.WriteLine("prop-stmt:" + Q.H.Data.P + "," + Q.H.Data.N);
                    Q.H = new Holder(); Q.H.Data.P = 10;
                    int r = (Q.H.Data.P += Q.Right());
                    Console.WriteLine("prop-expr:" + r + "," + Q.H.Data.P + "," + Q.H.Data.N);
                    Q.H = new Holder();
                    Q.H.Data.Name ??= Q.Name();
                    Console.WriteLine("coalesce-stmt:" + Q.H.Data.Name + "," + Q.H.Data.N);
                    Q.H = new Holder();
                    string? c = (Q.H.Data.Name ??= Q.Name());
                    Console.WriteLine("coalesce-expr:" + c + "," + Q.H.Data.Name + "," + Q.H.Data.N);
                }
            }
            """, "P");
        Assert.Equal("""
            prop-stmt:17,100
            prop-expr:17,17,100
            coalesce-stmt:set,100
            coalesce-expr:set,set,100
            """.ReplaceLineEndings("\n"), output);
    }

    // 右辺が struct local 自体を差し替える (`s.X = (s = t).X`)
    [CFact]
    public void StructLocalAssign_RhsReplacesLocal()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct S { public int N; public int Other; }
            public class P
            {
                public static void Main()
                {
                    S t = new S { N = 2, Other = 9 };
                    S a = new S { N = 1 };
                    int r = (a = t).N;
                    S b = new S { N = 1, Other = 1 };
                    b.Other = (b = t).N + 10;
                    S c = new S { N = 1, Other = 1 };
                    c.Other += (c = t).N;
                    S d = new S { N = 1 };
                    int rd = (d.Other = (d = t).N + 20);
                    Console.WriteLine(r + "," + a.N + "|" + Show(b) + "|" + Show(c) + "|" + Show(d) + "," + rd);
                }
                static string Show(S s) => s.N + "," + s.Other;
            }
            """, "P");
        Assert.Equal("2,2|2,12|2,3|2,22,22", output.ReplaceLineEndings("\n"));
    }
}
