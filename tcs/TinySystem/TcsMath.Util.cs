using System.Numerics;

namespace TinySystem;

// .NET implementation of the Math transcendental functions. It copies the same steps as runtime/math (C) and returns the same results as the tcs C
// and VM paths, bit for bit. float / double arithmetic is IEEE-754, and
// RyuJIT does not fuse multiply-add. The transcendental functions of .NET Math / MathF are not used.
internal static partial class TcsMath
{
    static uint AsUInt(float f) => BitConverter.SingleToUInt32Bits(f);
    static float AsFloat(uint u) => BitConverter.UInt32BitsToSingle(u);
    static ulong AsULong(double f) => BitConverter.DoubleToUInt64Bits(f);
    static double AsDouble(ulong u) => BitConverter.UInt64BitsToDouble(u);

    static readonly float NaN = AsFloat(0x7fc00000u);
    static readonly float Inf = AsFloat(0x7f800000u);

    // Rounds x with |x| < 2^51 to an integer, rounding to even
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

    // Same as tcs_fmaf in runtime/math/tcs_math_internal.h: a correctly rounded fmaf
    // using only double arithmetic
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

    // For p, a * b rounded to double, returns a * b - p exactly
    static double MulErr(double a, double b, double p)
    {
        double ah = SplitHi(a), al = a - ah;
        double bh = SplitHi(b), bl = b - bh;
        return (((ah * bh - p) + ah * bl) + al * bh) + al * bl;
    }

    // For q, y / x rounded to double, returns y - q * x exactly
    static double DivRem(double y, double x, double q)
    {
        double p = q * x;
        return (y - p) - MulErr(q, x, p);
    }
}
