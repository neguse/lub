namespace TinyCs.Tcs2c.Tests;

// pattern / switch の意味論の 2 backend differential。期待値は同じ program を
// CoreCLR で実行した stdout
public partial class DifferentialTests
{
    // section ごとに同じ名前の pattern 変数 (T283)。C の出力が全ての section で最後の x を参照しないこと
    [CFact]
    public void SwitchSections_SameNamePatternVariables()
    {
        var output = Backends.AssertParity("""
            using System;
            public class A { public int X = 1; }
            public class B { public string S = "b"; }
            public class P
            {
                static string F(object o)
                {
                    switch (o)
                    {
                        case A x: x.X = x.X + 10; return "A" + x.X;
                        case B x: x.S = x.S + "!"; return "B" + x.S;
                    }
                    return "none";
                }
                static string G(object o) => o switch { A x => "A" + x.X, B x => "B" + x.S, _ => "none" };
                public static void Main()
                {
                    Console.WriteLine(F(new A()) + "," + F(new B()) + "," + F("s"));
                    Console.WriteLine(G(new A()) + "," + G(new B()));
                }
            }
            """, "P");
        Assert.Equal("A11,Bb!,none\nA1,Bb", output);
    }

    [CFact]
    public void SwitchExpression_GuardedDiscardArm()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static int calls;
                static bool Guard(bool result) { calls++; return result; }
                static int Pick(int x) => x switch { _ when Guard(true) => 11, _ => 22 };
                static int PickFalse(int x) => x switch { _ when Guard(false) => 11, _ => 22 };
                static int Mixed(int x) => x switch { 1 => 1, _ when Guard(x > 5) => 2, 3 => 3, _ => 4 };
                public static void Main()
                {
                    Console.WriteLine(Pick(7) + ":" + calls);
                    Console.WriteLine(PickFalse(7) + ":" + calls);
                    Console.WriteLine(Mixed(1) + "," + Mixed(9) + "," + Mixed(3) + "," + Mixed(4) + ":" + calls);
                }
            }
            """, "P");
        Assert.Equal("11:1\n22:2\n1,2,3,4:5", output);
    }

    [CFact]
    public void PropertyPattern_TestsNonNull()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box { public int Value = 1; public Box Inner; }
            public struct Pt { public int X; }
            public class P
            {
                static int Pick(Box b) => b switch { { } => 11, _ => 22 };
                static int Deep(Box b) => b switch { { Inner: { } } => 1, { } => 2, _ => 3 };
                public static void Main()
                {
                    Box none = null;
                    var box = new Box();
                    var nested = new Box { Inner = new Box() };
                    Console.WriteLine((none is { } ? 1 : 0) + "," + (box is { } ? 1 : 0));
                    Console.WriteLine(Pick(none) + "," + Pick(box));
                    Console.WriteLine((none is { Value: 1 } ? 1 : 0) + "," + (box is { Value: 1 } ? 1 : 0));
                    Console.WriteLine(Deep(none) + "," + Deep(box) + "," + Deep(nested));
                    int? n = null;
                    int? m = 4;
                    Console.WriteLine((n is { } ? 1 : 0) + "," + (m is { } ? 1 : 0));
                    Pt? p = null;
                    Pt? q = new Pt { X = 2 };
                    Console.WriteLine((p is { X: 2 } ? 1 : 0) + "," + (q is { X: 2 } ? 1 : 0));
                    var v = new Pt();
                    Console.WriteLine(v is { } ? 1 : 0);
                }
            }
            """, "P");
        Assert.Equal("0,1\n22,11\n0,1\n3,2,1\n0,1\n0,1\n1", output);
    }

    [CFact]
    public void IsPattern_EvaluatesInputOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box { public int A; public int B; }
            public class Holder
            {
                public int Reads;
                Box box = new Box { A = 1, B = 2 };
                public Box Item { get { Reads++; return box; } }
            }
            public class P
            {
                static int calls;
                static int Next() { calls += 1; return calls; }
                static Box Make() { calls += 1; return new Box { A = 1, B = 2 }; }
                public static void Main()
                {
                    Console.WriteLine((Next() is > 0 and < 2 ? 1 : 0) + ":" + calls);
                    Console.WriteLine((Next() is 1 or 2 ? 1 : 0) + ":" + calls);
                    Console.WriteLine((Next() is not 1 and not 2 ? 1 : 0) + ":" + calls);
                    Console.WriteLine((Make() is { A: 1, B: 2 } ? 1 : 0) + ":" + calls);
                    Console.WriteLine((Make() is Box { A: 1 } ? 1 : 0) + ":" + calls);
                    var h = new Holder();
                    Console.WriteLine((h is { Item: { A: 1, B: 2 } } ? 1 : 0) + ":" + h.Reads);
                }
            }
            """, "P");
        Assert.Equal("1:1\n1:2\n1:3\n1:4\n1:5\n1:1", output);
    }

    [CFact]
    public void SwitchStatement_CaseSharingDefaultSection()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                static int calls;
                static bool Guard(bool result) { calls++; return result; }
                public static void Main()
                {
                    for (int input = 0; input < 3; input++)
                    {
                        switch (input)
                        {
                            case 1:
                            default: Console.WriteLine("A"); break;
                            case > 0: Console.WriteLine("B"); break;
                        }
                    }
                    for (int round = 0; round < 2; round++)
                    {
                        foreach (var x in new[] { 0, 4, 5, 6 })
                        {
                            switch (x)
                            {
                                case 0: Console.WriteLine("zero"); break;
                                case 5 when Guard(round == 0):
                                default: Console.WriteLine("D" + x); break;
                                case > 3: Console.WriteLine("big" + x); break;
                            }
                        }
                    }
                    Console.WriteLine(calls);
                    for (int input = 0; input < 4; input++)
                    {
                        switch (input)
                        {
                            case 2:
                            default:
                                var sum = 0;
                                for (int i = 0; i < 5; i++) { if (i == 1) continue; sum += i; }
                                if (input == 2) { Console.WriteLine("early" + sum); break; }
                                Console.WriteLine("loop" + sum);
                                break;
                            case > 1: Console.WriteLine("gt" + input); break;
                        }
                    }
                }
            }
            """, "P");
        Assert.Equal("A\nA\nB\nzero\nbig4\nD5\nbig6\nzero\nbig4\nbig5\nbig6\n2\nloop9\nloop9\nearly9\ngt3", output);
    }

    // field を入力にした照合は、途中の getter が field を差し替えても最初に読んだ object を照合する (#88)
    [CFact]
    public void PropertyPattern_FieldInputReadOnce()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Box
            {
                public int Value;
                public int Reset { get { P.Current = new Box { Value = 9 }; return 1; } }
            }
            public class Holder { public Box Item; }
            public static class P
            {
                public static Box Current;
                public static void Main()
                {
                    Current = new Box { Value = 2 };
                    Console.WriteLine(Current is { Reset: 1, Value: 2 });
                    Console.WriteLine(Current.Value);
                    var h = new Holder { Item = new Box { Value = 3 } };
                    Current = h.Item;
                    Console.WriteLine(h.Item is { Value: 3 } or { Value: 4 });
                    Console.WriteLine(Current is { Value: 3, Reset: 1 } ? Current.Value : -1);
                }
            }
            """, "P");
        Assert.Equal("True\n9\nTrue\n9", output);
    }
}
