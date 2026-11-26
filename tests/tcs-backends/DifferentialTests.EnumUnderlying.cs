namespace TinyCs.Tcs2c.Tests;

// An enum value follows its underlying type (byte / sbyte / short / ushort / int / uint). The tcs VM and C hold the value as 32 bit two's complement, but
// conversions to and operations on 8 / 16 bit underlying types (cast, ++ / --, ~, + / -, += / -=) wrap into the range of the underlying type as .NET's unchecked does, and
// the ordering comparison of a uint enum, List.Sort without a comparison, format D, and the stringification of a value without a name use the unsigned value.
// The whole name table is searched whatever the number of members. The expected value is the stdout of the same program run on CoreCLR
public class EnumUnderlyingDifferentialTests
{
    [CFact]
    public void EnumUnderlying_SmallBaseWrapsLikeDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public enum B : byte { Zero = 0, One = 1, Max = 255 }
            public enum SB : sbyte { Min = -128, MOne = -1, Zero = 0, Max = 127 }
            public enum US : ushort { Zero = 0, Max = 65535 }
            public enum SS : short { Min = -32768, Zero = 0, Max = 32767 }
            public enum I { Big = 300 }
            public class Box { public B Value = B.Max; }
            public static class P
            {
                static int Id(int x) { return x; }
                static char C(char c) { return c; }
                public static void Main()
                {
                    int v256 = Id(256), v255 = Id(255), v65536 = Id(65536), v65535 = Id(65535);
                    Console.WriteLine((int)(B)v256 + " " + (int)(SB)v255 + " " + (int)(US)v65536 + " " + (int)(SS)v65535);
                    Console.WriteLine((int)(B)Id(int.MinValue) + " " + (int)(SB)Id(int.MaxValue) + " " + (int)(US)Id(int.MaxValue) + " " + (int)(SS)Id(int.MinValue) + " " + (int)(B)Id(-1) + " " + (int)(SB)Id(-129));
                    Console.WriteLine((int)(B)Id(255) + " " + (int)(SB)Id(-128) + " " + (int)(US)Id(65535) + " " + (int)(SS)Id(-32768));
                    Console.WriteLine((B)v256 + " " + (SB)v255 + " " + (US)v65536 + " " + (SS)v65535 + " " + US.Max + " " + (int)US.Max);
                    I big = I.Big;
                    SB mone = SB.MOne;
                    B bmax = B.Max;
                    Console.WriteLine((int)(B)big + " " + (int)(SB)big + " " + (int)(SB)C('A') + " " + (int)(B)mone + " " + (int)(SB)bmax);
                    B b = B.Max;
                    b++;
                    Console.WriteLine(b == B.Zero);
                    b--;
                    Console.WriteLine((int)b);
                    B z = B.Zero;
                    z--;
                    Console.WriteLine((int)z + " " + (int)~B.Zero + " " + (int)~z);
                    B zero = B.Zero;
                    B max = B.Max;
                    Console.WriteLine((int)(zero - B.One) + " " + (int)(zero - 1) + " " + (int)(max + 1) + " " + (int)(1 + max));
                    B c = B.Max;
                    c += 2;
                    Console.WriteLine((int)c);
                    c -= 5;
                    Console.WriteLine((int)c);
                    B pre = B.Max;
                    Console.WriteLine((int)(++pre) + " " + (int)(pre++) + " " + (int)pre + " " + (int)(--pre) + " " + (int)(pre--) + " " + (int)pre);
                    var box = new Box();
                    box.Value++;
                    box.Value += 3;
                    Console.WriteLine((int)box.Value);
                    SB s = SB.Max;
                    s++;
                    Console.WriteLine((int)s + " " + s);
                    s--;
                    Console.WriteLine((int)s + " " + s);
                    SB sm = SB.Min;
                    Console.WriteLine((int)(sm - SB.Max) + " " + (int)(SB.Zero - sm) + " " + (int)~SB.Max);
                    US u = US.Zero;
                    u--;
                    Console.WriteLine((int)u + " " + u + " " + (int)~US.Zero);
                    SS ss = SS.Max;
                    ss++;
                    Console.WriteLine((int)ss + " " + ss + " " + (int)(ss - SS.Max));
                    I wide = (I)Id(int.MaxValue);
                    wide++;
                    Console.WriteLine((int)wide);
                    int? ni = Id(300);
                    int? none = null;
                    SB? ns = (SB?)ni;
                    SB? nn = (SB?)none;
                    B? nb = (B?)Id(257);
                    Console.WriteLine((int)ns.Value + " " + nn.HasValue + " " + (int)nb.Value);
                    Console.WriteLine((int)unchecked((B)256) + " " + (int)unchecked((SB)200));
                    const B K = unchecked((B)257);
                    B one = B.One;
                    switch (one)
                    {
                        case K: Console.WriteLine("K"); break;
                        default: Console.WriteLine("other"); break;
                    }
                }
            }
            """, "P");
        Assert.Equal("""
            0 -1 0 -1
            0 -1 65535 0 255 127
            255 -128 65535 -32768
            Zero MOne Zero -1 Max 65535
            44 44 65 255 -1
            True
            255
            255 255 0
            255 255 0 0
            1
            252
            0 0 1 0 0 255
            3
            -128 Min
            127 Max
            1 -128 -128
            65535 Max 65535
            -32768 Min 1
            -2147483648
            44 False 1
            0 -56
            K
            """.ReplaceLineEndings("\n"), output);
    }

    // A uint enum value with the high bit set (0x80000000 or more) is also ordered as unsigned. A negative value of an int enum stays signed
    [CFact]
    public void EnumUnderlying_UIntRelationsAreUnsigned()
    {
        var output = Backends.AssertParity("""
            using System;
            public enum E : uint { Low = 0, One = 1, Mid = 0x7fffffff, High = 0x80000000, Top = 0xffffffff }
            public enum N { Neg = -1, Zero = 0, Pos = 1 }
            public static class P
            {
                static string Kind(E e)
                {
                    switch (e)
                    {
                        case < E.Mid: return "small";
                        case E.Mid: return "mid";
                        case >= E.Top: return "top";
                        default: return "high";
                    }
                }
                public static void Main()
                {
                    E value = E.High;
                    Console.WriteLine(value > E.Low);
                    Console.WriteLine((value < E.Low) + " " + (value <= E.Mid) + " " + (value >= E.One) + " " + (E.Mid < value) + " " + (E.Top > value));
                    E mid = E.Mid;
                    E top = E.Top;
                    Console.WriteLine((mid > E.One) + " " + (top >= value) + " " + (value == E.High) + " " + (value != top));
                    N neg = N.Neg;
                    Console.WriteLine((neg < N.Zero) + " " + (N.Pos > neg));
                    Console.WriteLine((value is > E.Mid) + " " + (value is > E.Mid and < E.Top) + " " + (mid is >= E.High));
                    Console.WriteLine(value switch { < E.Mid => "small", _ => "big" });
                    Console.WriteLine(Kind(E.Low) + " " + Kind(E.Mid) + " " + Kind(E.High) + " " + Kind(E.Top));
                    int count = 0;
                    for (E e = E.Mid; e <= E.High; e++) count++;
                    Console.WriteLine(count);
                }
            }
            """, "P");
        Assert.Equal("""
            True
            False False True True True
            True True True True
            True True
            True True False
            big
            small mid high top
            2
            """.ReplaceLineEndings("\n"), output);
    }

    // Sort without a comparison puts a uint enum in unsigned order, as .NET's default comparer does.
    // Values up to 0x7fffffff and negative values of an sbyte enum are in the same order as a signed comparison
    [CFact]
    public void EnumUnderlying_UIntSortIsUnsigned()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum E : uint { Low = 1, Mid = 0x7fffffff, High = 0x80000000, Top = 0xffffffff }
            public enum C : uint { Low = 1, High = 0x7fffffff }
            public enum SB : sbyte { Neg = -5, MOne = -1, Zero = 0, Pos = 3 }
            public static class P
            {
                static void SortAll<T>(List<T> xs) { xs.Sort(); }
                public static void Main()
                {
                    var xs = new List<E> { E.High, E.Low };
                    xs.Sort();
                    Console.WriteLine(xs[0]);
                    Console.WriteLine(xs[1]);
                    var cs = new List<C> { C.High, C.Low };
                    cs.Sort();
                    Console.WriteLine(cs[0] + " " + cs[1]);
                    var all = new List<E> { E.Top, E.High, E.Low, E.Mid, E.High, (E)0x80000001 };
                    all.Sort();
                    Console.WriteLine(string.Join(",", all));
                    var gs = new List<E> { E.Top, E.High, E.Low, E.Mid };
                    SortAll(gs);
                    Console.WriteLine(string.Join(",", gs));
                    var ts = new List<E> { E.Top, E.High, E.Low, E.Mid };
                    TinySystem.List.Sort(ts);
                    Console.WriteLine(string.Join(",", ts));
                    var bs = new List<E> { E.Top, E.Low, E.High };
                    bs.Sort((a, b) => a == b ? 0 : a < b ? -1 : 1);
                    Console.WriteLine(string.Join(",", bs));
                    var ss = new List<SB> { SB.Pos, SB.Neg, SB.Zero, SB.MOne };
                    ss.Sort();
                    Console.WriteLine(string.Join(",", ss));
                }
            }
            """, "P");
        Assert.Equal("""
            Low
            High
            Low High
            Low,Mid,High,High,2147483649,Top
            Low,Mid,High,Top
            Low,Mid,High,Top
            Low,High,Top
            Neg,MOne,Zero,Pos
            """.ReplaceLineEndings("\n"), output);
    }

    // Format D of a uint enum and a value no name can represent (ToString, interpolation, Flags, string.Join, a generic T) are unsigned numbers.
    // A negative value of an sbyte or int enum is a signed number
    [CFact]
    public void EnumUnderlying_UIntNumbersAreUnsigned()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            public enum U : uint { Low = 1, Mid = 0x7fffffff, High = 0x80000000, Top = 0xffffffff }
            [Flags] public enum F : uint { A = 1, Hi = 0x80000000 }
            public enum S : sbyte { M = -1, Z = 0 }
            public enum N { Neg = -3 }
            public static class P
            {
                static U Pick(int i) { return (U)i; }
                static string Name<T>(T value) { return value + ""; }
                public static void Main()
                {
                    Console.WriteLine(U.High.ToString("D"));
                    Console.WriteLine($"{U.High:D}");
                    Console.WriteLine(U.Top.ToString("d") + " " + $"{U.Mid:d}" + " " + U.Low.ToString("D") + " " + $"{U.Top:D}");
                    U undefined = Pick(-2);
                    Console.WriteLine(undefined + " " + undefined.ToString() + " " + undefined.ToString("G") + " " + $"{undefined}" + " " + undefined.ToString("F") + " " + undefined.ToString("D"));
                    Console.WriteLine((F)Pick(-2) + " " + (F.A | F.Hi) + " " + (F)3 + " " + ((F)Pick(-2)).ToString("D"));
                    U? some = U.High;
                    U? none = null;
                    Console.WriteLine($"{some:D}|{none:D}|{some}|" + some.ToString() + "|" + none.ToString() + "|");
                    Console.WriteLine(string.Join(",", new List<U> { Pick(-3), U.High, U.Low }));
                    Console.WriteLine(Name(Pick(-4)) + " " + Name(U.Top));
                    S m = S.M;
                    N n = N.Neg;
                    Console.WriteLine(m.ToString("D") + " " + $"{m:D}" + " " + n.ToString("D") + " " + $"{n:d}" + " " + ((S)(-7)) + " " + ((N)(-9)));
                }
            }
            """, "P");
        Assert.Equal("""
            2147483648
            2147483648
            4294967295 2147483647 1 4294967295
            4294967294 4294967294 4294967294 4294967294 4294967294 4294967294
            4294967294 A, Hi 3 4294967294
            2147483648||High|High||
            4294967293,High,Low
            4294967292 Top
            -1 -1 -3 -3 -7 -9
            """.ReplaceLineEndings("\n"), output);
    }

    // An enum with 257 or more members also looks the name up in the whole name table (also for [Flags] combinations)
    [CFact]
    public void EnumUnderlying_ManyMembersKeepNames()
    {
        var members = new List<string>();
        for (int i = 0; i < 257; i++) members.Add("M" + i);
        var flags = new List<string>();
        for (int i = 0; i < 300; i++) flags.Add("G" + i);
        var output = Backends.AssertParity($$"""
            using System;
            public enum E { {{string.Join(", ", members)}} }
            [Flags] public enum G { {{string.Join(", ", flags)}} }
            public static class P
            {
                static int Id(int x) { return x; }
                public static void Main()
                {
                    Console.WriteLine(E.M256);
                    Console.WriteLine(E.M0 + " " + E.M255 + " " + (E)Id(257) + " " + $"{E.M256}" + " " + E.M256.ToString("G"));
                    Console.WriteLine(G.G299 + " " + (G)Id(511) + " " + (G)Id(0) + " " + (G)Id(300) + " " + (G)Id(1024));
                }
            }
            """, "P");
        Assert.Equal("""
            M256
            M0 M255 257 M256 M256
            G299 G212, G299 G0 G4, G296 1024
            """.ReplaceLineEndings("\n"), output);
    }
}
