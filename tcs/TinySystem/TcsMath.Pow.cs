namespace TinySystem;

// powf: runtime/math/powf.c の C# 版 (musl の Arm 由来、Copyright (c)
// 2017-2018, Arm Limited. SPDX-License-Identifier: MIT)
internal static partial class TcsMath
{
    static readonly double[] PowLog2Poly =
        [0.288457581109214, -0.36092606229713164, 0.480898481472577, -0.7213474675006291, 1.4426950408774342];
    const double PowExp2ShiftScaled = 6755399441055744.0 / 32;
    static readonly double[] PowExp2Poly = [0.05550361559341535, 0.2402284522445722, 0.6931471806916203];
    const uint PowSignBias = 1 << (5 + 11);

    // log2 の表は log2f と同じ (Log2Tab)
    static double PowLog2Inline(uint ix)
    {
        uint tmp = ix - LogOff;
        int i = (int)((tmp >> (23 - 4)) % 16);
        uint top = tmp & 0xff800000;
        uint iz = ix - top;
        int k = (int)top >> 23;
        double invc = Log2Tab[i, 0], logc = Log2Tab[i, 1];
        double z = AsFloat(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k;
        double r2 = r * r;
        double y = PowLog2Poly[0] * r + PowLog2Poly[1];
        double p = PowLog2Poly[2] * r + PowLog2Poly[3];
        double r4 = r2 * r2;
        double q = PowLog2Poly[4] * r + y0;
        q = p * r2 + q;
        y = y * r4 + q;
        return y;
    }

    static float PowExp2Inline(double xd, uint signBias)
    {
        double kd = xd + PowExp2ShiftScaled;
        ulong ki = AsULong(kd);
        kd -= PowExp2ShiftScaled;
        double r = xd - kd;
        ulong t = Exp2fTab[ki % 32];
        ulong ski = ki + signBias;
        t += ski << (52 - 5);
        double s = AsDouble(t);
        double z = PowExp2Poly[0] * r + PowExp2Poly[1];
        double r2 = r * r;
        double y = PowExp2Poly[2] * r + 1;
        y = z * r2 + y;
        y = y * s;
        return (float)y;
    }

    // 0: 整数でない、1: 奇数、2: 偶数 (iy は 0 でない有限の値の bit)
    static int PowCheckInt(uint iy)
    {
        int e = (int)(iy >> 23 & 0xff);
        if (e < 0x7f)
            return 0;
        if (e > 0x7f + 23)
            return 2;
        if ((iy & ((1u << (0x7f + 23 - e)) - 1)) != 0)
            return 0;
        if ((iy & (1u << (0x7f + 23 - e))) != 0)
            return 1;
        return 2;
    }

    static bool PowZeroInfNan(uint ix) => 2 * ix - 1 >= 2u * 0x7f800000 - 1;

    public static float Pow(float x, float y)
    {
        uint signBias = 0;
        uint ix = AsUInt(x), iy = AsUInt(y);
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000 || PowZeroInfNan(iy))
        {
            if (PowZeroInfNan(iy))
            {
                if (2 * iy == 0)
                    return 1.0f;
                if (ix == 0x3f800000)
                    return 1.0f;
                if (2 * ix > 2u * 0x7f800000 || 2 * iy > 2u * 0x7f800000)
                    return x + y;
                if (2 * ix == 2 * 0x3f800000)
                    return 1.0f;
                if ((2 * ix < 2 * 0x3f800000) == ((iy & 0x80000000) == 0))
                    return 0.0f;
                return y * y;
            }
            if (PowZeroInfNan(ix))
            {
                float x2 = x * x;
                if ((ix & 0x80000000) != 0 && PowCheckInt(iy) == 1)
                    x2 = -x2;
                return (iy & 0x80000000) != 0 ? 1 / x2 : x2;
            }
            if ((ix & 0x80000000) != 0)
            {
                int yint = PowCheckInt(iy);
                if (yint == 0)
                    return NaN;
                if (yint == 1)
                    signBias = PowSignBias;
                ix &= 0x7fffffff;
            }
            if (ix < 0x00800000)
            {
                ix = AsUInt(x * 8388608.0f);
                ix &= 0x7fffffff;
                ix -= 23 << 23;
            }
        }
        double logx = PowLog2Inline(ix);
        double ylogx = y * logx;
        if ((AsULong(ylogx) >> 47 & 0xffff) >= AsULong(126.0) >> 47)
        {
            if (ylogx > 127.99999995700433)
                return signBias != 0 ? -Inf : Inf;
            if (ylogx <= -150.0)
                return signBias != 0 ? -0.0f : 0.0f;
        }
        return PowExp2Inline(ylogx, signBias);
    }
}
