namespace TinyCs.Tcs2c.Tests;

// ±0 と NaN の扱い (il-spec §6 / §13: 規範は TinySystem dotnet facade)。
// 期待値は同じ program を CoreCLR で実行した stdout
public partial class DifferentialTests
{
    [CFact]
    public void Math_SignedZeroAndNaN_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            public class P
            {
                public static string B(bool b) => b ? "1" : "0";
                public static string Show(float x) => x != x ? "NaN" : x == 0.0f && 1.0f / x < 0.0f ? "-0" : "" + x;
                public static void Main()
                {
                    float p = 0.0f;
                    float n = -1.0f * p;
                    float nan = p / p;
                    Console.WriteLine("ts-min " + Show(TinySystem.Math.Min(p, n)) + " " + Show(TinySystem.Math.Min(n, p)) + " " + Show(TinySystem.Math.Min(1.0f, nan)) + " " + Show(TinySystem.Math.Min(nan, 1.0f)) + " " + Show(TinySystem.Math.Min(2.0f, 1.0f)) + " " + TinySystem.Math.Min(7, 3));
                    Console.WriteLine("ts-max " + Show(TinySystem.Math.Max(p, n)) + " " + Show(TinySystem.Math.Max(n, p)) + " " + Show(TinySystem.Math.Max(1.0f, nan)) + " " + Show(TinySystem.Math.Max(nan, 1.0f)) + " " + Show(TinySystem.Math.Max(1.0f, 2.0f)) + " " + TinySystem.Math.Max(3, 7));
                    Console.WriteLine("sys-min " + Show(Math.Min(p, n)) + " " + Show(Math.Min(n, p)) + " " + Show(Math.Min(1.0f, nan)) + " " + Show(Math.Min(nan, 1.0f)) + " " + Show(Math.Min(2.0f, 1.0f)) + " " + Math.Min(7, 3));
                    Console.WriteLine("sys-max " + Show(Math.Max(p, n)) + " " + Show(Math.Max(n, p)) + " " + Show(Math.Max(1.0f, nan)) + " " + Show(Math.Max(nan, 1.0f)) + " " + Show(Math.Max(1.0f, 2.0f)) + " " + Math.Max(3, 7));
                    Console.WriteLine("ts-floor " + Show(TinySystem.Math.Floor(n)) + " " + Show(TinySystem.Math.Floor(p)) + " " + Show(TinySystem.Math.Floor(-0.25f)) + " " + Show(TinySystem.Math.Floor(2.5f)) + " " + Show(TinySystem.Math.Floor(-2.5f)));
                    Console.WriteLine("ts-ceil " + Show(TinySystem.Math.Ceil(n)) + " " + Show(TinySystem.Math.Ceil(p)) + " " + Show(TinySystem.Math.Ceil(-0.25f)) + " " + Show(TinySystem.Math.Ceil(2.5f)) + " " + Show(TinySystem.Math.Ceil(-2.5f)));
                    Console.WriteLine("sys-floor " + Show((float)Math.Floor(n)) + " " + Show((float)Math.Floor(p)) + " " + Show((float)Math.Floor(-0.25f)) + " " + Show((float)Math.Floor(2.5f)) + " " + Show((float)Math.Floor(-2.5f)));
                    Console.WriteLine("sys-ceil " + Show((float)Math.Ceiling(n)) + " " + Show((float)Math.Ceiling(p)) + " " + Show((float)Math.Ceiling(-0.25f)) + " " + Show((float)Math.Ceiling(2.5f)) + " " + Show((float)Math.Ceiling(-2.5f)));
                    Console.WriteLine("ts-round " + Show(TinySystem.Math.Round(-0.25f)) + " " + Show(TinySystem.Math.Round(-0.5f)) + " " + Show(TinySystem.Math.Round(n)) + " " + Show(TinySystem.Math.Round(0.25f)) + " " + Show(TinySystem.Math.Round(2.5f)) + " " + Show(TinySystem.Math.Round(3.5f)) + " " + Show(TinySystem.Math.Round(-1.5f)) + " " + Show(TinySystem.Math.Round(-2.5f)));
                    Console.WriteLine("ts-round-digits " + Show(TinySystem.Math.Round(-0.25f, 0)) + " " + Show(TinySystem.Math.Round(-0.025f, 1)) + " " + Show(TinySystem.Math.Round(n, 1)) + " " + Show(TinySystem.Math.Round(0.025f, 1)) + " " + Show(TinySystem.Math.Round(1.25f, 1)));
                    Console.WriteLine("sys-round " + Show((float)Math.Round(-0.25f)) + " " + Show((float)Math.Round(-0.5f)) + " " + Show((float)Math.Round(n)) + " " + Show((float)Math.Round(2.5f)) + " " + Show((float)Math.Round(-2.5f)));
                    Console.WriteLine("ts-recip " + B(1.0f / TinySystem.Math.Round(-0.25f) < 0.0f) + " " + B(1.0f / TinySystem.Math.Ceil(-0.25f) < 0.0f) + " " + B(1.0f / TinySystem.Math.Min(p, n) < 0.0f));
                }
            }
            """, "P");
        Assert.Equal("""
            ts-min -0 -0 NaN NaN 1 3
            ts-max 0 0 NaN NaN 2 7
            sys-min -0 -0 NaN NaN 1 3
            sys-max 0 0 NaN NaN 2 7
            ts-floor -0 0 -1 2 -3
            ts-ceil -0 0 -0 3 -2
            sys-floor -0 0 -1 2 -3
            sys-ceil -0 0 -0 3 -2
            ts-round -0 -0 -0 0 2 4 -2 -2
            ts-round-digits -0 -0 -0 0 1.2
            sys-round -0 -0 -0 2 -2
            ts-recip 1 1 1
            """.ReplaceLineEndings("\n"), output);
    }

    [CFact]
    public void Linq_MinMaxAndListSearch_NaN_MatchDotnet()
    {
        var output = Backends.AssertParity("""
            using System;
            using System.Collections.Generic;
            using TinySystem.Linq;
            public class P
            {
                public static string B(bool b) => b ? "1" : "0";
                public static string Show(float x) => x != x ? "NaN" : x == 0.0f && 1.0f / x < 0.0f ? "-0" : "" + x;
                public static void Main()
                {
                    float p = 0.0f;
                    float n = -1.0f * p;
                    float nan = p / p;
                    var a = new List<float> { 1.0f, nan };
                    var b = new List<float> { nan, 1.0f };
                    var all = new List<float> { nan, nan };
                    var finite = new List<float> { 2.0f, 1.0f, 3.0f };
                    var zeros = new List<float> { p, n };
                    var ints = new List<int> { 4, 2, 9 };
                    Console.WriteLine("min " + Show(a.Min()) + " " + Show(b.Min()) + " " + Show(all.Min()) + " " + Show(finite.Min()) + " " + Show(zeros.Min()) + " " + ints.Min());
                    Console.WriteLine("max " + Show(a.Max()) + " " + Show(b.Max()) + " " + Show(all.Max()) + " " + Show(finite.Max()) + " " + Show(zeros.Max()) + " " + ints.Max());
                    Console.WriteLine("min-sel " + Show(a.Min(x => x)) + " " + Show(b.Min(x => x)) + " " + Show(all.Min(x => x)) + " " + Show(finite.Min(x => x)) + " " + ints.Min(x => -x));
                    Console.WriteLine("max-sel " + Show(a.Max(x => x)) + " " + Show(b.Max(x => x)) + " " + Show(all.Max(x => x)) + " " + Show(finite.Max(x => x)) + " " + ints.Max(x => -x));
                    var calls = 0;
                    var c = new List<float> { 1.0f, nan, 0.5f, 2.0f };
                    Console.WriteLine("min-stop " + Show(c.Min(x => { calls++; return x; })) + " " + calls);
                    var values = new List<float> { 2.0f, nan, 3.0f, nan };
                    Console.WriteLine("find " + B(values.Contains(nan)) + " " + values.IndexOf(nan) + " " + B(values.Contains(3.0f)) + " " + values.IndexOf(3.0f) + " " + B(values.Contains(4.0f)) + " " + values.IndexOf(4.0f));
                    Console.WriteLine("remove " + B(values.Remove(nan)) + " " + values.Count + " " + values.IndexOf(nan) + " " + B(values.Remove(nan)) + " " + B(values.Remove(nan)) + " " + values.Count);
                    var noNan = new List<float> { 1.0f, p };
                    Console.WriteLine("absent " + B(noNan.Contains(nan)) + " " + noNan.IndexOf(nan) + " " + B(noNan.Remove(nan)) + " " + B(noNan.Contains(n)) + " " + noNan.IndexOf(n));
                }
            }
            """, "P");
        Assert.Equal("""
            min NaN NaN NaN 1 0 2
            max 1 1 NaN 3 0 9
            min-sel NaN NaN NaN 1 -9
            max-sel 1 1 NaN 3 -2
            min-stop NaN 2
            find 1 1 1 2 0 -1
            remove 1 3 2 1 0 2
            absent 0 -1 0 1 1
            """.ReplaceLineEndings("\n"), output);
    }
}
