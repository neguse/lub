namespace TinyCs.Tcs2c.Tests;

// pattern / switch の意味論の 2 backend differential。期待値は同じ program を
// CoreCLR で実行した stdout
public partial class DifferentialTests
{
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
}
