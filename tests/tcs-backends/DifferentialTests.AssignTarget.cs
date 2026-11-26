namespace TinyCs.Tcs2c.Tests;

// The assignment target (receiver / index / struct storage location) is evaluated only once, before the right-hand side,
// and the value of an assignment in expression position is the assigned value (the left side is not rebuilt and read). The expected values are the
// stdout of running the same program on CoreCLR
public class AssignTargetDifferentialTests
{
    // Even if the right side replaces the receiver, the expression value is the assigned value and the receiver is evaluated once
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

    // The receiver of a custom property assignment in expression position, and an index that a collection method changes
    // (`A[L.Count] = L.Remove(5) ? ..`), are also fixed before the right side
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

    // Even if the right side replaces a struct's storage location (a class field / array element / static field / a field of
    // this / a field of a captured local), the write reaches the current storage location
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

    // custom properties / ??= on a struct also write to the current storage location
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

    // The right side replaces the struct local itself (`s.X = (s = t).X`)
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

    // Even if the right side's with / initializer / `??` reads the target local, the local keeps its previous value until the right side finishes (#433)
    [CFact]
    public void AssignLocal_RightSideReadsTargetBeforeConstruction()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public record R(int X, int Y);
            public static class P
            {
                static int Param(int[] a) { a = new int[] { a[1], a[0] }; return a[0] * 10 + a[1]; }
                public static void Main()
                {
                    var r = new R(1, 3);
                    r = r with { X = 2, Y = r.X };
                    Console.WriteLine(r.X + ":" + r.Y);
                    int[] values = new int[] { 3, 7 };
                    values = new int[] { values[1], values[0] };
                    Console.WriteLine(values[0] + ":" + values[1]);
                    var list = new List<int> { 3, 7 };
                    list = new List<int> { list[1], list[0], list.Count };
                    Console.WriteLine(list[0] + ":" + list[1] + ":" + list[2]);
                    var dict = new Dictionary<int, int> { { 1, 2 } };
                    dict = new Dictionary<int, int> { { 5, dict.Count } };
                    Console.WriteLine(dict[5]);
                    bool c = true;
                    values = c ? new int[] { values[1] } : null;
                    Console.WriteLine(values[0]);
                    Console.WriteLine(Param(new int[] { 5, 6 }));
                    string s = "a";
                    string n = null;
                    s = n ?? s + "b";
                    Console.WriteLine(s);
                    int i = 1;
                    i = i + 1;
                    i = -i;
                    Console.WriteLine(i);
                }
            }
            """, "P");
        Assert.Equal("2:1\n7:3\n7:3:2\n1\n3\n65\nab\n-2", output);
    }

    // Even when a property getter on the right side rewrites the captured local used as the index, the assignment target is fixed by the index before the right side, and the value of the expression is the assigned value (a getter can rewrite locals just like a call)
    [CFact]
    public void AssignIndex_RhsGetterChangesCapturedIndex()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public struct Slot { public int N; }
            public class Trigger { public Func<int> Go; public int Value { get { return Go(); } } }
            public class P
            {
                static Func<int> f;
                static int Value { get { return f(); } }
                public static void Main()
                {
                    Slot[] slots = new Slot[2];
                    int i = 0;
                    var t = new Trigger();
                    t.Go = () => { i = 1; return 7; };
                    slots[i].N = t.Value;
                    Console.WriteLine("struct:" + slots[0].N + "," + slots[1].N + "," + i);
                    slots = new Slot[2]; i = 0;
                    int r = (slots[i].N = t.Value);
                    Console.WriteLine("struct-expr:" + r + "," + slots[0].N + "," + slots[1].N);
                    slots = new Slot[2]; i = 0;
                    slots[i].N += t.Value;
                    Console.WriteLine("struct-compound:" + slots[0].N + "," + slots[1].N);
                    int[] a = new int[] { 0, 9 };
                    int j = 0;
                    f = () => { j = 1; return 7; };
                    Console.WriteLine("expr:" + (a[j] = Value) + "," + a[0] + "," + a[1]);
                    a = new int[] { 1, 9 }; j = 0;
                    a[j] += Value;
                    Console.WriteLine("compound:" + a[0] + "," + a[1]);
                    a = new int[] { 1, 9 }; j = 0;
                    Console.WriteLine("compound-expr:" + (a[j] += Value) + "," + a[0] + "," + a[1]);
                    var l = new List<int> { 0, 9 }; j = 0;
                    Console.WriteLine("list:" + (l[j] = Value) + "," + l[0] + "," + l[1]);
                    j = 0;
                    Console.WriteLine("binary:" + (j + Value));
                    a = new int[] { 0, 9 }; j = 0;
                    a[j] = f();
                    Console.WriteLine("direct:" + a[0] + "," + a[1]);
                }
            }
            """, "P");
        Assert.Equal("""
            struct:7,0,1
            struct-expr:7,7,0
            struct-compound:7,0
            expr:7,7,9
            compound:8,9
            compound-expr:8,8,9
            list:7,7,9
            binary:7
            direct:7,9
            """.ReplaceLineEndings("\n"), output);
    }
}
