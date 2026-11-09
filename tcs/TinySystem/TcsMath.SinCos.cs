namespace TinySystem;

// sinf / cosf: runtime/math/sincosf.c の C# 版 (CORE-MATH 由来、MIT。
// 著作権表示と許諾文は sincosf.c の先頭)
internal static partial class TcsMath
{
    static double RbigSinCos(uint u, out int q)
    {
        int e = (int)((u >> 23) & 0xff), i;
        ulong m = (u & (~0u >> 9)) | 1u << 23;
        Mul64(m, 0xfe5163abdebbc562ul, out ulong p0h);
        ulong p1l = Mul64(m, 0xdb6295993c439041ul, out ulong p1h);
        p1l += p0h;
        p1h += p1l < p0h ? 1ul : 0ul;
        ulong p2l = Mul64(m, 0xfc2757d1f534ddc0ul, out ulong p2h);
        p2l += p1h;
        p2h += p2l < p1h ? 1ul : 0ul;
        ulong p3l = Mul64(m, 0xa2f9836e4e441529ul, out ulong p3h);
        p3l += p2h;
        p3h += p3l < p2h ? 1ul : 0ul;
        long a;
        int k = e - 124, s = k - 23;
        if (s < 64)
        {
            i = (int)(uint)(p3h << s | p3l >> (64 - s));
            a = (long)(p3l << s | p2l >> (64 - s));
        }
        else if (s == 64)
        {
            i = (int)(uint)p3l;
            a = (long)p2l;
        }
        else
        {
            i = (int)(uint)(p3l << (s - 64) | p2l >> (128 - s));
            a = (long)(p2l << (s - 64) | p1l >> (128 - s));
        }
        int sgn = -(int)(u >> 31);
        long sm = -(long)((ulong)a >> 63);
        i = (int)(i - sm);
        double z = (double)(a ^ (long)sgn) * 5.421010862427522e-20;
        i = (i ^ sgn) - sgn;
        q = i;
        return z;
    }

    static double RltlSinCos(float z, out int q)
    {
        double x = z;
        double idl = -3.1558305786379073e-09 * x, idh = 5.092958182096481 * x,
            id = RoundevenSmall(idh);
        q = (int)(uint)AsULong(6755399441055744.0 + id);
        return (idh - id) + idl;
    }

    static double Rltl0SinCos(double x, out int q)
    {
        double idh = 5.092958178940651 * x, id = RoundevenSmall(idh);
        q = (int)(uint)AsULong(6755399441055744.0 + id);
        return idh - id;
    }

    static readonly double[] SinCosB =
        [0.019276571095877645, -6.193103220211784e-05, 7.958785981094399e-08, -5.4777514393633976e-11];
    static readonly double[] SinCosA =
        [0.19634954084936204, -0.0012616486279372187, 2.432025854080733e-06, -2.2318367225754577e-09];
    // sin(i * pi/16)。cos の表 (CosTable) は 8 つずらした cos(i * pi/16)
    static readonly double[] SinTable =
    [
        0.0, 0.19509032201612828, 0.3826834323650898, 0.5555702330196022,
        0.7071067811865476, 0.8314696123025452, 0.9238795325112867, 0.9807852804032304,
        1.0, 0.9807852804032304, 0.9238795325112867, 0.8314696123025452,
        0.7071067811865476, 0.5555702330196022, 0.3826834323650898, 0.19509032201612828,
        0.0, -0.19509032201612828, -0.3826834323650898, -0.5555702330196022,
        -0.7071067811865476, -0.8314696123025452, -0.9238795325112867, -0.9807852804032304,
        -1.0, -0.9807852804032304, -0.9238795325112867, -0.8314696123025452,
        -0.7071067811865476, -0.5555702330196022, -0.3826834323650898, -0.19509032201612828,
    ];
    static readonly double[] CosTable =
    [
        1.0, 0.9807852804032304, 0.9238795325112867, 0.8314696123025452,
        0.7071067811865476, 0.5555702330196022, 0.3826834323650898, 0.19509032201612828,
        0.0, -0.19509032201612828, -0.3826834323650898, -0.5555702330196022,
        -0.7071067811865476, -0.8314696123025452, -0.9238795325112867, -0.9807852804032304,
        -1.0, -0.9807852804032304, -0.9238795325112867, -0.8314696123025452,
        -0.7071067811865476, -0.5555702330196022, -0.3826834323650898, -0.19509032201612828,
        0.0, 0.19509032201612828, 0.3826834323650898, 0.5555702330196022,
        0.7071067811865476, 0.8314696123025452, 0.9238795325112867, 0.9807852804032304,
    ];

    // 多項式の近似で丸めを誤る入力と、その正しい値 (rh + rl)
    static readonly float[] SinArg = [9830.398f, 0.72992426f, 1.3086903f, 9.424778f];
    static readonly float[] SinRh = [-0.34761325f, 0.6668132f, 0.9658464f, -2.384976e-08f];
    static readonly float[] SinRl = [-7.450581e-09f, -1.4901161e-08f, -1.4901161e-08f, -4.440892e-16f];

    static float SinDatabase(float x, double r)
    {
        uint ax = AsUInt(x) & (~0u >> 1);
        for (int i = 0; i < 4; i++)
        {
            if (AsUInt(SinArg[i]) == ax)
            {
                float sgn = (AsUInt(x) >> 31) != 0 ? -1.0f : 1.0f;
                return sgn * SinRh[i] + sgn * SinRl[i];
            }
        }
        return (float)r;
    }

    static float SinBig(float x)
    {
        uint u = AsUInt(x);
        uint ax = u << 1;
        if (ax >= 0xffu << 24)
            return (ax << 8) != 0 ? x + x : NaN;
        double z = RbigSinCos(u, out int ia);
        double z2 = z * z, z4 = z2 * z2;
        double aa = (SinCosA[0] + z2 * SinCosA[1]) + z4 * (SinCosA[2] + z2 * SinCosA[3]);
        double bb = (SinCosB[0] + z2 * SinCosB[1]) + z4 * (SinCosB[2] + z2 * SinCosB[3]);
        double s0 = SinTable[ia & 31], c0 = SinTable[(int)((uint)(ia + 8) & 31)];
        double r = s0 + z * (aa * c0 - bb * (z * s0));
        return (float)r;
    }

    public static float Sin(float x)
    {
        uint ax = AsUInt(x) << 1;
        int ia;
        double z0 = x, z;
        if (ax > 0x99000000u || ax < 0x73000000u)
        {
            if (ax < 0x73000000u)
            {
                if (ax < 0x66000000u)
                {
                    if (ax == 0u)
                        return x;
                    return Fmaf(-x, AsFloat(AsUInt(x) & 0x7fffffffu), x);
                }
                return (-0.16666667f * x) * (x * x) + x;
            }
            return SinBig(x);
        }
        if (ax < 0x822d97c8u)
        {
            if (ax == 0x7e75b8a2u || ax == 0x7f4f0654u)
                return SinDatabase(x, 0.0);
            z = Rltl0SinCos(z0, out ia);
        }
        else
        {
            if (ax == 0x8c333330u)
                return SinDatabase(x, 0.0);
            z = RltlSinCos(x, out ia);
        }
        double z2 = z * z, z4 = z2 * z2;
        double aa = (SinCosA[0] + z2 * SinCosA[1]) + z4 * (SinCosA[2] + z2 * SinCosA[3]);
        double bb = (SinCosB[0] + z2 * SinCosB[1]) + z4 * (SinCosB[2] + z2 * SinCosB[3]);
        double s0 = SinTable[ia & 31], c0 = SinTable[(ia + 8) & 31];
        double r = s0 + aa * (z * c0) - bb * (z2 * s0);
        return (float)r;
    }

    static readonly float[] CosArg = [4.712389f, 2.8616508e+15f, 2.3127222e+16f, 1.1004678e+19f, 1.7269983e+20f];
    static readonly float[] CosRh = [1.1924881e-08f, 0.5339164f, 0.48877442f, 0.9964101f, 0.969058f];
    static readonly float[] CosRl = [-2.220446e-16f, -1.4901161e-08f, 7.450581e-09f, 1.4901161e-08f, -1.4901161e-08f];

    static float CosDatabase(float x, double r)
    {
        uint ax = AsUInt(x) & (~0u >> 1);
        for (int i = 0; i < 5; i++)
        {
            if (AsUInt(CosArg[i]) == ax)
                return CosRh[i] + CosRl[i];
        }
        return (float)r;
    }

    static float CosBig(float x)
    {
        uint u = AsUInt(x);
        uint ax = u << 1;
        if (ax >= 0xffu << 24)
            return (ax << 8) != 0 ? x + x : NaN;
        double z = RbigSinCos(u, out int ia);
        double z2 = z * z, z4 = z2 * z2;
        double aa = (SinCosA[0] + z2 * SinCosA[1]) + z4 * (SinCosA[2] + z2 * SinCosA[3]);
        double bb = (SinCosB[0] + z2 * SinCosB[1]) + z4 * (SinCosB[2] + z2 * SinCosB[3]);
        double s0 = CosTable[(int)((uint)(ia + 8) & 31)], c0 = CosTable[ia & 31];
        double r = c0 + z * (aa * s0 - bb * (z * c0));
        ulong tail = (AsULong(r) + 6) & (~0ul >> 36);
        if (tail <= 12)
            return CosDatabase(x, r);
        return (float)r;
    }

    public static float Cos(float x)
    {
        uint ax = AsUInt(x) << 1;
        int ia;
        double z0 = x, z;
        if (ax > 0x99000000u || ax < 0x74000000u)
        {
            if (ax < 0x74000000u)
                return 0.5f * Fmaf(x, -x, 2.0f);
            return CosBig(x);
        }
        if (ax < 0x82a41896u)
        {
            if (ax == 0x812d97c8u)
                return CosDatabase(x, 0.0);
            z = Rltl0SinCos(z0, out ia);
        }
        else
        {
            z = RltlSinCos(x, out ia);
        }
        double z2 = z * z, z4 = z2 * z2;
        double aa = (SinCosA[0] + z2 * SinCosA[1]) + z4 * (SinCosA[2] + z2 * SinCosA[3]);
        double bb = (SinCosB[0] + z2 * SinCosB[1]) + z4 * (SinCosB[2] + z2 * SinCosB[3]);
        double c0 = CosTable[ia & 31], s0 = CosTable[(ia + 8) & 31];
        double r = c0 + aa * (z * s0) - bb * (z2 * c0);
        return (float)r;
    }
}
