namespace TinyCs.Tcs2c.Tests;

// A property pattern with a variable (`Rect { W: 2 } r`, `{ W: 2 } r`) binds the input to the variable when it matches
// (the value of the written type if a type is written, the inner value for a value-type T? input, a copy for a struct). Same as the declaration pattern `Rect r`
public class PropertyPatternDesignationDifferentialTests
{
    [CFact]
    public void PropertyPatternDesignation_BindsInput()
    {
        var output = Backends.AssertParity("""
            using System;
            public class Rect { public int W; public int H; }
            public class P
            {
                static int StatTyped(object o)
                {
                    switch (o)
                    {
                        case Rect { W: 1 } r: return r.H;
                        case Rect { W: 2 } r: return r.H * 10;
                    }
                    return 0;
                }

                static int ArmTyped(object o) => o switch { Rect { W: 2 } r when r.H > 5 => r.H, Rect { } r => -r.H, _ => 0 };
                static int IsTyped(object o) => o is Rect { W: 2 } r ? r.H : 0;

                static int Stat(Rect? o)
                {
                    switch (o)
                    {
                        case { W: 2 } r: return r.H;
                        default: return 0;
                    }
                }

                static int Arm(Rect? o) => o switch { { W: 2 } r => r.H, { } r => -r.H, null => 99 };
                static int Is(Rect? o) => o is { W: 2 } r ? r.H : 0;

                static int calls;

                static Rect Make()
                {
                    calls++;
                    return new Rect { W = 2, H = 3 };
                }

                public static void Main()
                {
                    var a = new Rect { W = 2, H = 5 };
                    var b = new Rect { W = 2, H = 7 };
                    var c = new Rect { W = 1, H = 4 };
                    Console.WriteLine(StatTyped(a) + " " + StatTyped(c) + " " + StatTyped("x"));
                    Console.WriteLine(ArmTyped(a) + " " + ArmTyped(b) + " " + ArmTyped(c) + " " + ArmTyped(1));
                    Console.WriteLine(IsTyped(a) + " " + IsTyped(c) + " " + IsTyped("x"));
                    Console.WriteLine(Stat(a) + " " + Stat(c) + " " + Stat(null));
                    Console.WriteLine(Arm(a) + " " + Arm(c) + " " + Arm(null));
                    Console.WriteLine(Is(a) + " " + Is(c) + " " + Is(null));
                    object o = b;
                    if (o is Rect { H: > 6 } big) Console.WriteLine(big.W);
                    Console.WriteLine(Make() is { W: 2 } m ? m.H : 0);
                    Console.WriteLine(calls);
                }
            }
            """, "P");
        Assert.Equal(string.Join("\n", "50 4 0", "-5 7 -4 0", "5 0 0", "5 0 0", "5 -4 99", "5 0 0", "2", "3", "1"), output);
    }

    [CFact]
    public void PropertyPatternDesignation_ValueTypes()
    {
        var output = Backends.AssertParity("""
            using System;
            public struct Pt { public int X; public int Y; }
            public class P
            {
                static int Is(Pt p) => p is { X: 1 } q ? q.Y : 0;
                static int Arm(Pt p) => p switch { { X: 1 } q => q.Y, { } q => -q.Y };

                static int Stat(Pt p)
                {
                    switch (p)
                    {
                        case { X: 1 } q: return q.Y;
                        default: return 0;
                    }
                }

                static int NullableIs(Pt? p) => p is { X: 1 } q ? q.Y : 0;
                static int NullableArm(Pt? p) => p switch { { X: 1 } q => q.Y, Pt { } q => -q.Y, null => 99 };
                static int IntIs(int? n) => n is { } v ? v + 1 : 0;
                static int IntArm(int? n) => n switch { { } v => v * 2, null => -1 };

                public static void Main()
                {
                    var p = new Pt { X = 1, Y = 2 };
                    var r = new Pt { X = 3, Y = 4 };
                    Console.WriteLine(Is(p) + " " + Is(r));
                    Console.WriteLine(Arm(p) + " " + Arm(r));
                    Console.WriteLine(Stat(p) + " " + Stat(r));
                    Console.WriteLine(NullableIs(p) + " " + NullableIs(r) + " " + NullableIs(null));
                    Console.WriteLine(NullableArm(p) + " " + NullableArm(r) + " " + NullableArm(null));
                    Console.WriteLine(IntIs(4) + " " + IntIs(null));
                    Console.WriteLine(IntArm(4) + " " + IntArm(null));
                    // What is bound is a copy, and rewriting the variable does not change the input
                    if (p is { X: 1 } copy)
                    {
                        copy.Y = 9;
                        Console.WriteLine(p.Y + " " + copy.Y);
                    }
                    Pt? np = p;
                    if (np is { } inner)
                    {
                        inner.Y = 8;
                        Console.WriteLine(np.Value.Y + " " + inner.Y);
                    }
                }
            }
            """, "P");
        Assert.Equal(string.Join("\n", "2 0", "2 -4", "2 0", "2 0 0", "2 -4 99", "5 0", "8 -1", "2 9", "2 8"), output);
    }
}
