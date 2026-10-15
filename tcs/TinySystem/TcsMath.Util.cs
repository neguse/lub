using System.Numerics;

namespace TinySystem;

// Math の超越関数の .NET 実装。runtime/math (C) と同じ手順を写し、tcs2c と
// Lua の経路と bit 単位で同じ結果を返す。float / double の演算は IEEE-754 で、
// RyuJIT は積和を融合しない。.NET の Math / MathF の超越関数は使わない。
internal static partial class TcsMath
{
    static uint AsUInt(float f) => BitConverter.SingleToUInt32Bits(f);
    static float AsFloat(uint u) => BitConverter.UInt32BitsToSingle(u);
    static ulong AsULong(double f) => BitConverter.DoubleToUInt64Bits(f);
    static double AsDouble(ulong u) => BitConverter.UInt64BitsToDouble(u);

    static readonly float NaN = AsFloat(0x7fc00000u);
    static readonly float Inf = AsFloat(0x7f800000u);

    // |x| < 2^51 の x を、偶数への丸めで整数にする
    static double RoundevenSmall(double x)
    {
        const double shift = 6755399441055744.0; // 1.5 * 2^52
        return (x + shift) - shift;
    }

    static ulong Mul64(ulong a, ulong b, out ulong hi)
    {
        hi = global::System.Math.BigMul(a, b, out ulong lo);
        return lo;
    }

    static int Clz32(uint x) => BitOperations.LeadingZeroCount(x);

    // runtime/math/tcs_math_internal.h の tcs_fmaf と同じ: double の演算だけで
    // 正しく丸めた fmaf
    static float Fmaf(float a, float b, float c)
    {
        double p = (double)a * (double)b;
        double dc = c;
        double s = p + dc;
        double bb = s - p;
        double e = (p - (s - bb)) + (dc - bb);
        if (e != 0)
        {
            ulong u = AsULong(s);
            if ((u & 1) == 0)
                u += ((e > 0) == (s > 0)) ? 1ul : ulong.MaxValue;
            s = AsDouble(u);
        }
        return (float)s;
    }

    static double SplitHi(double a)
    {
        double c = 134217729.0 * a; // 2^27 + 1
        return c - (c - a);
    }

    // a * b を double に丸めた p に対して a * b - p を正確に返す
    static double MulErr(double a, double b, double p)
    {
        double ah = SplitHi(a), al = a - ah;
        double bh = SplitHi(b), bl = b - bh;
        return (((ah * bh - p) + ah * bl) + al * bh) + al * bl;
    }

    // q = y / x を double に丸めた q に対して y - q * x を正確に返す
    static double DivRem(double y, double x, double q)
    {
        double p = q * x;
        return (y - p) - MulErr(q, x, p);
    }
}
