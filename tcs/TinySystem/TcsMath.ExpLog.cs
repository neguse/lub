namespace TinySystem;

// expf / logf / log2f / powf: runtime/math の expf.c / logf.c / log2f.c /
// powf.c の C# 版 (musl の Arm 由来、Copyright (c) 2017-2018, Arm Limited.
// SPDX-License-Identifier: MIT)
internal static partial class TcsMath
{
    static readonly ulong[] Exp2fTab =
    [
        0x3ff0000000000000, 0x3fefd9b0d3158574, 0x3fefb5586cf9890f, 0x3fef9301d0125b51,
        0x3fef72b83c7d517b, 0x3fef54873168b9aa, 0x3fef387a6e756238, 0x3fef1e9df51fdee1,
        0x3fef06fe0a31b715, 0x3feef1a7373aa9cb, 0x3feedea64c123422, 0x3feece086061892d,
        0x3feebfdad5362a27, 0x3feeb42b569d4f82, 0x3feeab07dd485429, 0x3feea47eb03a5585,
        0x3feea09e667f3bcd, 0x3fee9f75e8ec5f74, 0x3feea11473eb0187, 0x3feea589994cce13,
        0x3feeace5422aa0db, 0x3feeb737b0cdc5e5, 0x3feec49182a3f090, 0x3feed503b23e255d,
        0x3feee89f995ad3ad, 0x3feeff76f2fb5e47, 0x3fef199bdd85529c, 0x3fef3720dcef9069,
        0x3fef5818dcfba487, 0x3fef7c97337b9b5f, 0x3fefa4afa2a490da, 0x3fefd0765b6e4540,
    ];

    const double ExpShift = 6755399441055744.0;
    const double ExpInvLn2Scaled = 1.4426950408889634 * 32;
    static readonly double[] ExpPolyScaled =
        [0.05550361559341535 / 32 / 32 / 32, 0.2402284522445722 / 32 / 32, 0.6931471806916203 / 32];

    static uint Top12(float x) => AsUInt(x) >> 20;

    public static float Exp(float x)
    {
        double xd = x;
        uint abstop = Top12(x) & 0x7ff;
        if (abstop >= Top12(88.0f))
        {
            if (AsUInt(x) == 0xff800000u)
                return 0.0f;
            if (abstop >= Top12(Inf))
                return x + x;
            if (x > 88.72283f)
                return Inf;
            if (x < -103.97208f)
                return 0.0f;
        }
        double z = ExpInvLn2Scaled * xd;
        double kd = z + ExpShift;
        ulong ki = AsULong(kd);
        kd -= ExpShift;
        double r = z - kd;
        ulong t = Exp2fTab[ki % 32];
        t += ki << (52 - 5);
        double s = AsDouble(t);
        z = ExpPolyScaled[0] * r + ExpPolyScaled[1];
        double r2 = r * r;
        double y = ExpPolyScaled[2] * r + 1;
        y = z * r2 + y;
        y = y * s;
        return (float)y;
    }

    const uint LogOff = 0x3f330000;
    static readonly double[,] LogTab =
    {
        { 1.398907162146528, -0.33569133332882284 }, { 1.3403141896637998, -0.2929040563774074 },
        { 1.286432210124115, -0.2518726580937369 }, { 1.2367150214269895, -0.21245868807117255 },
        { 1.1906977166711752, -0.17453945183745634 }, { 1.1479821020556429, -0.1380057072319758 },
        { 1.1082251448272158, -0.10275976698545139 }, { 1.0711297413057381, -0.06871392447020525 },
        { 1.036437278977283, -0.0357891387398228 }, { 1.0, 0.0 },
        { 0.9492859795739057, 0.05204517742929496 }, { 0.8951049428609004, 0.11081431298787942 },
        { 0.8476821620351103, 0.1652495223695143 }, { 0.8050314851692001, 0.21687389031699977 },
        { 0.7664671008843108, 0.2659635028121397 }, { 0.731428603316328, 0.3127556664073557 },
    };
    const double LogLn2 = 0.6931471805599453;
    static readonly double[] LogPoly = [-0.25089342214237154, 0.333456765744066, -0.4999997485802103];

    public static float Log(float x)
    {
        uint ix = AsUInt(x);
        if (ix == 0x3f800000)
            return 0;
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000)
        {
            if (ix * 2 == 0)
                return -Inf;
            if (ix == 0x7f800000)
                return x;
            if ((ix & 0x80000000) != 0 || ix * 2 >= 0xff000000)
                return NaN;
            ix = AsUInt(x * 8388608.0f);
            ix -= 23 << 23;
        }
        uint tmp = ix - LogOff;
        int i = (int)((tmp >> (23 - 4)) % 16);
        int k = (int)tmp >> 23;
        uint iz = ix - (tmp & 0xff800000);
        double invc = LogTab[i, 0], logc = LogTab[i, 1];
        double z = AsFloat(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k * LogLn2;
        double r2 = r * r;
        double y = LogPoly[1] * r + LogPoly[2];
        y = LogPoly[0] * r2 + y;
        y = y * r2 + (y0 + r);
        return (float)y;
    }

    static readonly double[,] Log2Tab =
    {
        { 1.398907162146528, -0.48430022186289673 }, { 1.3403141896637998, -0.42257122959194704 },
        { 1.286432210124115, -0.3633754347673556 }, { 1.2367150214269895, -0.30651309567405577 },
        { 1.1906977166711752, -0.25180720160537634 }, { 1.1479821020556429, -0.19910014943794563 },
        { 1.1082251448272158, -0.14825100623281615 }, { 1.0711297413057381, -0.09913323807318392 },
        { 1.036437278977283, -0.051632812977629436 }, { 1.0, 0.0 },
        { 0.9492859795739057, 0.07508531937943004 }, { 0.8951049428609004, 0.15987125980713107 },
        { 0.8476821620351103, 0.2384046664317681 }, { 0.8050314851692001, 0.31288288605863257 },
        { 0.7664671008843108, 0.38370422656453185 }, { 0.731428603316328, 0.451211048935815 },
    };
    static readonly double[] Log2Poly = [-0.36051725506874704, 0.4811247078767291, -0.7213476299867769, 1.4426950186867042];

    public static float Log2(float x)
    {
        uint ix = AsUInt(x);
        if (ix == 0x3f800000)
            return 0;
        if (ix - 0x00800000 >= 0x7f800000 - 0x00800000)
        {
            if (ix * 2 == 0)
                return -Inf;
            if (ix == 0x7f800000)
                return x;
            if ((ix & 0x80000000) != 0 || ix * 2 >= 0xff000000)
                return NaN;
            ix = AsUInt(x * 8388608.0f);
            ix -= 23 << 23;
        }
        uint tmp = ix - LogOff;
        int i = (int)((tmp >> (23 - 4)) % 16);
        uint top = tmp & 0xff800000;
        uint iz = ix - top;
        int k = (int)tmp >> 23;
        double invc = Log2Tab[i, 0], logc = Log2Tab[i, 1];
        double z = AsFloat(iz);
        double r = z * invc - 1;
        double y0 = logc + (double)k;
        double r2 = r * r;
        double y = Log2Poly[1] * r + Log2Poly[2];
        y = Log2Poly[0] * r2 + y;
        double p = Log2Poly[3] * r + y0;
        y = y * r2 + p;
        return (float)y;
    }
}
