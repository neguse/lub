namespace TinySystem;

// tanf: runtime/math/tanf.c の C# 版 (CORE-MATH 由来、MIT。著作権表示と
// 許諾文は tanf.c の先頭)
internal static partial class TcsMath
{
    static double RltlTan(float z, out int q)
    {
        double x = z;
        double idl = -3.944788223297384e-10 * x, idh = 0.6366197727620602 * x,
            id = RoundevenSmall(idh);
        q = (int)(long)id;
        return (idh - id) + idl;
    }

    static double RbigTan(uint u, out int q)
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
        int k = e - 127, s = k - 23;
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

    static readonly double[] TanCn = [1.5707963267948966, -0.49720165641032027, 0.02683402276915988, -0.00017660096093977045];
    static readonly double[] TanCd = [1.0, -1.1389954387488281, 0.1421268437745497, -0.0031314039049681057];
    static readonly double[] TanSw = [0, 1];

    // 有理式の近似で丸めを誤る入力と、その正しい値 (rh + rl)
    static readonly float[] TanArg =
        [1.0790827f, 2.2526088e+08f, 4.7430978e+14f, 4.512996e+15f, 9.3166134e+21f, 6.9559356e+25f, 1.2778496e+26f, 7.1710355e+30f];
    static readonly float[] TanRh =
        [1.8670954f, 0.238287f, 0.34455025f, 1.7895327f, 0.6748017f, -3.9000707f, -0.88550705f, -1.8912677f];
    static readonly float[] TanRl =
        [-3.409719e-16f, 4.358793e-18f, -3.268032e-17f, 2.1771658e-16f, 1.7449128e-16f, 8.1730744e-16f, -1.2783293e-16f, -2.478792e-16f];

    public static float Tan(float x)
    {
        uint u = AsUInt(x);
        int e = (int)((u >> 23) & 0xff), i;
        double z;
        if (e < 127 + 28)
        {
            if (e < 115)
            {
                if (e < 102)
                    return Fmaf(x, AsFloat(u & 0x7fffffffu), x);
                float x2 = x * x;
                return Fmaf(x, 0.33333334f * x2, x);
            }
            z = RltlTan(x, out i);
        }
        else if (e < 0xff)
        {
            z = RbigTan(u, out i);
        }
        else
        {
            return (u << 9) != 0 ? x + x : NaN;
        }
        double z2 = z * z, z4 = z2 * z2;
        double n = TanCn[0] + z2 * TanCn[1], n2 = TanCn[2] + z2 * TanCn[3];
        n += z4 * n2;
        double d = TanCd[0] + z2 * TanCd[1], d2 = TanCd[2] + z2 * TanCd[3];
        d += z4 * d2;
        n *= z;
        double s0 = TanSw[i & 1], s1 = TanSw[1 - (i & 1)];
        double r1 = (n * s1 - d * s0) / (n * s0 + d * s1);
        ulong tail = (AsULong(r1) + 7) & (~0ul >> 35);
        if (tail <= 14)
        {
            uint ax = u & (~0u >> 1), sgn = u >> 31;
            for (int j = 0; j < 8; j++)
            {
                if (AsUInt(TanArg[j]) == ax)
                    return sgn != 0 ? -TanRh[j] - TanRl[j] : TanRh[j] + TanRl[j];
            }
        }
        return (float)r1;
    }
}
