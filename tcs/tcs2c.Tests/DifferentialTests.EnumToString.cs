namespace TinyCs.Tcs2c.Tests;

// enum の文字列化 (ToString / 文字列連結 / 補間 / Console.WriteLine) は .NET の
// Enum.ToString と同じく member 名、[Flags] (と書式 F) は名前の ", " 連結、表せない
// 値は数値 (#133)。書式 D は数値。uint / byte / sbyte の enum も同じ。同じ値の
// member と並びは .NET の EnumInfo (値の符号なし昇順) に従う。期待値は同じ
// program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Enums_ToStringMatchesDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public enum Color { Red, Green, Blue }
            public enum Dup { A = 1, B = 1, C = 2, D = 0 }
            public enum Neg { M = -1, Z = 0, P = 1 }
            [Flags] public enum Fl { None = 0, A = 1, B = 2, AB = 3, C = 4, Big = 16 }
            [Flags] public enum NoZero { X = 1, Y = 2 }
            [Flags] public enum WithNeg { A = 1, All = -1 }
            [Flags] public enum Ov { A = 1, B = 3, C = 6 }
            [System.Flags] public enum Sorted { Z = 0, Z2 = 0, A = 1, A2 = 1, B = 2 }
            public enum Many { M0, M1, M2, M3, M4, M5, M6, M7, M8, M9, M10, M11, M12, M13, M14, M15, M16, M17, D7 = 7, M19 }
            public enum Many2 { X0 = 5, X1 = 1, X2 = 5, X3 = 2, X4, X5, X6, X7, X8, X9, X10, X11, X12, X13, X14, X15, X16, X17, X18, X19, X20, X21, X22, X23, X24, X25, X26, X27, X28, X29, X30, X31, X32, X33 }
            public enum Empty { }
            public enum U : uint { A = 1, Hi = 0x80000000 }
            [Flags] public enum UF : uint { A = 1, Top = 0x80000000 }
            public enum B : byte { X = 1, Y = 200 }
            public enum S : sbyte { M = -1, N = 3 }
            public class Holder { public Color Tint = Color.Blue; public Color Get() { return Tint; } }
            public static class P
            {
                static Color Pick() { return Color.Green; }
                public static void Main()
                {
                    Color c = Color.Green;
                    Console.WriteLine(c.ToString());
                    Console.WriteLine(Pick().ToString());
                    Console.WriteLine("" + Pick());
                    Console.WriteLine(c);
                    Console.WriteLine(Color.Blue + "|" + ((Color)7) + "|" + ((Color)(-2)));
                    Console.WriteLine(Dup.A + "|" + Dup.B + "|" + ((Dup)0) + "|" + Dup.C);
                    Console.WriteLine(Neg.M + "|" + ((Neg)(-5)));
                    Console.WriteLine(((Fl)0) + "|" + (Fl.A | Fl.B) + "|" + (Fl.A | Fl.C) + "|" + ((Fl)7) + "|" + ((Fl)8) + "|" + ((Fl)21) + "|" + ((Fl)(-1)));
                    Console.WriteLine(((NoZero)0) + "|" + ((NoZero)3) + "|" + ((NoZero)4) + "|" + ((NoZero)5));
                    Console.WriteLine(((WithNeg)(-1)) + "|" + ((WithNeg)(-2)) + "|" + ((WithNeg)1) + "|" + ((WithNeg)0));
                    Console.WriteLine(((Ov)7) + "|" + ((Ov)5) + "|" + ((Ov)3));
                    Console.WriteLine(((Sorted)0) + "|" + ((Sorted)1) + "|" + ((Sorted)3));
                    Console.WriteLine(Many.D7 + "|" + Many.M7 + "|" + Many.M19);
                    Console.WriteLine(Many2.X0 + "|" + Many2.X2 + "|" + ((Many2)1));
                    Console.WriteLine("empty:" + ((Empty)0) + "|" + ((Empty)3));
                    Color? n = Color.Green;
                    Color? none = null;
                    Console.WriteLine(n + "|" + none + "|" + $"{n}" + "|" + n.ToString() + "|" + none.ToString() + "|");
                    Console.WriteLine(n);
                    Console.WriteLine($"{Color.Green}/{Fl.A | Fl.Big}/{c,7}|");
                    string s = "s:";
                    s += c;
                    s += Fl.A | Fl.C;
                    Console.WriteLine(s);
                    var h = new Holder();
                    Console.WriteLine(h.Tint + "|" + h.Get().ToString());
                    Console.WriteLine("fmt:" + c.ToString("G") + "|" + c.ToString("g") + "|" + c.ToString("D") + "|" + c.ToString("d") + "|" + c.ToString("F") + "|" + ((Color)3).ToString("F") + "|" + ((Color)3).ToString("G") + "|" + (Fl.A | Fl.C).ToString("D") + "|" + (Fl.A | Fl.C).ToString("F"));
                    Console.WriteLine($"interp:{c:G}|{c:F}|{c:D}|{(Color)3:F}|{(Color)3:g}|{Fl.AB | Fl.Big:F}|{n:G}|{none:G}|");
                    Console.WriteLine("unsigned:" + U.A + "|" + U.Hi + "|" + (UF)(1 | 0) + "|" + (UF.A | UF.Top) + "|" + ((UF)2));
                    Console.WriteLine("small:" + B.Y + "|" + (int)B.Y + "|" + S.M + "|" + (int)S.M + "|" + (S)3 + "|" + (B)7);
                    Console.WriteLine(Color.Red);
                }
            }
            """, "P");
        Assert.Equal("""
            Green
            Green
            Green
            Green
            Blue|7|-2
            A|A|D|C
            M|-5
            None|AB|A, C|AB, C|8|A, C, Big|-1
            0|X, Y|4|5
            All|-2|A|0
            A, C|5|B
            Z|A2|A2, B
            M7|M7|M19
            X2|X2|X1
            empty:0|3
            Green||Green|Green||
            Green
            Green/A, Big/  Green|
            s:GreenA, C
            Blue|Blue
            fmt:Green|Green|1|1|Green|Green, Blue|3|5|A, C
            interp:Green|Green|1|Green, Blue|3|AB, Big|Green||
            unsigned:A|Hi|A|A, Top|2
            small:Y|200|M|-1|N|7
            Red
            """.ReplaceLineEndings("\n"), output);
    }
}
