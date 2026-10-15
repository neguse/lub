namespace TinySystem;

// atan2f: runtime/math/atan2f.c の C# 版 (CORE-MATH 由来、MIT。著作権表示と
// 許諾文は atan2f.c の先頭)
internal static partial class TcsMath
{
    static double MulDD(double xh, double xl, double ch, double cl, out double l)
    {
        double ahlh = ch * xl, alhh = cl * xh, ahhh = ch * xh, ahhl = MulErr(ch, xh, ahhh);
        ahhl += alhh + ahlh;
        ch = ahhh + ahhl;
        l = (ahhh - ch) + ahhl;
        return ch;
    }

    static double PolyDD(double xh, double xl, int n, double[,] c, out double l)
    {
        int i = n - 1;
        double ch = c[i, 0], cl = c[i, 1];
        while (--i >= 0)
        {
            ch = MulDD(xh, xl, ch, cl, out cl);
            double th = ch + c[i, 0], tl = (c[i, 0] - th) + ch;
            ch = th;
            cl += tl + c[i, 1];
        }
        l = cl;
        return ch;
    }

    static float Atan2Tiny(float y, float x)
    {
        double dy = y, dx = x;
        double z = dy / dx;
        double e = DivRem(dy, dx, z);
        const double c = -0.3333333333333333;
        double zz = z * z;
        double cz = c * z;
        e = e / dx + cz * zz;
        ulong t = AsULong(z);
        if ((t & 0xfffffffUL) == 0)
        {
            if (z * e > 0)
                t += 1;
            else
                t -= 1;
        }
        return (float)AsDouble(t);
    }

    static readonly double[] Atan2Cn =
        [1.0, 2.506848521335565, 2.2855336234350774, 0.9227540611112051, 0.15965700667756133, 0.0093982071883745, 8.116266383809054e-05];
    static readonly double[] Atan2Cd =
        [1.0, 2.840181854668896, 3.03226090832491, 1.5083284691366383, 0.35061013533424623, 0.03311601651598859, 0.0008307046818566012];
    static readonly double[] Atan2M = [0, 1];
    const double Atan2Pi = 3.141592653589793, Atan2Pi2 = 1.5707963267948966, Atan2Pi2l = 6.123233995736766e-17;
    static readonly double[] Atan2Off = [0.0f, Atan2Pi2, Atan2Pi, Atan2Pi2, -0.0f, -Atan2Pi2, -Atan2Pi, -Atan2Pi2];
    static readonly double[] Atan2Offl = [0.0f, Atan2Pi2l, 2 * Atan2Pi2l, Atan2Pi2l, -0.0f, -Atan2Pi2l, -2 * Atan2Pi2l, -Atan2Pi2l];
    static readonly double[] Atan2Sgn = [1, -1];
    static readonly double[,] Atan2C =
    {
        { 1.0, -9.999371390980276e-27 }, { -0.3333333333333333, -1.8503696081891694e-17 },
        { 0.2, -1.1109570448589454e-17 }, { -0.14285714285714285, -6.906377603268134e-18 },
        { 0.11111111111111104, -6.04986207547041e-19 }, { -0.0909090909090874, -4.813186107306423e-18 },
        { 0.07692307692296789, 6.0635784048294865e-18 }, { -0.06666666666423005, 5.357368392422845e-19 },
        { 0.05882352937094788, -3.0234229312063074e-18 }, { -0.052631578418319884, 7.121906314230013e-19 },
        { 0.047619042181978474, 3.2606056478649954e-18 }, { -0.04347821570541953, 2.1474249882470664e-18 },
        { 0.039999692056834395, -1.4496021955960109e-18 }, { -0.037035291887394496, 8.394614713005368e-19 },
        { 0.03447445331733282, 3.57321674047701e-19 }, { -0.03222458593057924, -6.05919930630252e-19 },
        { 0.030187892413408284, 8.550359777848272e-19 }, { -0.028231467664296316, 8.645756239029875e-19 },
        { 0.026160406443643782, -1.1013132206776929e-18 }, { -0.02372363694711401, 5.82053126354201e-19 },
        { 0.0206884675894418, 8.78953163493978e-19 }, { -0.016981732349686016, 9.51948710384928e-19 },
        { 0.012817485672589527, -7.742239346647683e-19 }, { -0.008687313963361743, -5.63071564570556e-19 },
        { 0.005163618439611815, 3.2806307955849715e-19 }, { -0.00262691892479603, -1.373777510443008e-19 },
        { 0.0011135875725313154, -6.618746076431752e-20 }, { -0.0003808088264492978, 1.5620808802045265e-20 },
        { 0.00010056717515235685, -5.194627331100907e-21 }, { -1.919552508092125e-05, 8.202890655529531e-22 },
        { 2.35167210652331e-06, -1.3883775330103416e-22 }, { -1.3863591848022874e-07, -1.0947593470915086e-23 },
    };

    public static float Atan2(float y, float x)
    {
        uint ux = AsUInt(x), uy = AsUInt(y), ax = ux & (~0u >> 1), ay = uy & (~0u >> 1);
        if (ay >= (0xffu << 23) || ax >= (0xffu << 23))
        {
            if (ay > (0xffu << 23))
                return x + y;
            if (ax > (0xffu << 23))
                return x + y;
            bool yinf = ay == (0xffu << 23), xinf = ax == (0xffu << 23);
            if (yinf && xinf)
            {
                if ((ux >> 31) != 0)
                    return (float)(2.356194490192345 * Atan2Sgn[uy >> 31]);
                return (float)(0.7853981633974483 * Atan2Sgn[uy >> 31]);
            }
            if (xinf)
            {
                if ((ux >> 31) != 0)
                    return (float)(Atan2Pi * Atan2Sgn[uy >> 31]);
                return (float)(0.0 * Atan2Sgn[uy >> 31]);
            }
            return (float)(Atan2Pi2 * Atan2Sgn[uy >> 31]);
        }
        if (ay == 0)
        {
            if (ax == 0)
            {
                uint i0 = (uy >> 31) * 4 + (ux >> 31) * 2;
                if ((ux >> 31) != 0)
                    return (float)(Atan2Off[i0] + Atan2Offl[i0]);
                return (float)Atan2Off[i0];
            }
            if ((ux >> 31) == 0)
                return (float)(0.0 * Atan2Sgn[uy >> 31]);
        }
        uint gt = ay > ax ? 1u : 0u, i = (uy >> 31) * 4 + (ux >> 31) * 2 + gt;
        double zx = x, zy = y;
        double z = (Atan2M[gt] * zx + Atan2M[1 - gt] * zy) / (Atan2M[gt] * zy + Atan2M[1 - gt] * zx);
        double r;
        int d = (int)ax - (int)ay;
        if (d < (27 << 23) && d > (-(27 << 23)))
        {
            double z2 = z * z, z4 = z2 * z2, z8 = z4 * z4;
            double cn0 = Atan2Cn[0] + z2 * Atan2Cn[1];
            double cn2 = Atan2Cn[2] + z2 * Atan2Cn[3];
            double cn4 = Atan2Cn[4] + z2 * Atan2Cn[5];
            double cn6 = Atan2Cn[6];
            cn0 += z4 * cn2;
            cn4 += z4 * cn6;
            cn0 += z8 * cn4;
            double cd0 = Atan2Cd[0] + z2 * Atan2Cd[1];
            double cd2 = Atan2Cd[2] + z2 * Atan2Cd[3];
            double cd4 = Atan2Cd[4] + z2 * Atan2Cd[5];
            double cd6 = Atan2Cd[6];
            cd0 += z4 * cd2;
            cd4 += z4 * cd6;
            cd0 += z8 * cd4;
            r = cn0 / cd0;
        }
        else
        {
            r = 1;
        }
        z *= Atan2Sgn[gt];
        r = z * r + Atan2Off[i];
        if (((AsULong(r) + 8) & 0xfffffff) <= 16)
        {
            if (ay < ax && ((ax - ay) >> 23 >= 25))
                return Atan2Tiny(y, x);
            double zh, zl;
            if (gt == 0)
            {
                zh = zy / zx;
                zl = DivRem(zy, zx, zh) / zx;
            }
            else
            {
                zh = zx / zy;
                zl = DivRem(zx, zy, zh) / zy;
            }
            double z2h = MulDD(zh, zl, zh, zl, out double z2l);
            double ph = PolyDD(z2h, z2l, 32, Atan2C, out double pl);
            zh *= Atan2Sgn[gt];
            zl *= Atan2Sgn[gt];
            ph = MulDD(zh, zl, ph, pl, out pl);
            double sh = ph + Atan2Off[i], sl = ((Atan2Off[i] - sh) + ph) + pl + Atan2Offl[i];
            float rf = (float)sh;
            double th = rf, dh = sh - th, tm = dh + sl;
            if (th + th * 8.673617379884035e-19 == th - th * 8.673617379884035e-19)
            {
                ulong tth = AsULong(th);
                tth &= 0x7fful << 52;
                tth -= 24ul << 52;
                if ((tm < 0 ? -tm : tm) > AsDouble(tth))
                    tm *= 1.25;
                else
                    tm *= 0.75;
            }
            r = th + tm;
        }
        return (float)r;
    }
}
