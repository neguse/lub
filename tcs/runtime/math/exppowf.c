/* expf / powf。musl 1.2.6 の src/math/expf.c、powf.c、exp2f_data.c、
   powf_data.c (Arm の optimized-routines 由来) を、runtime/math の書き方に
   起こし直したもの。計算の手順は元のままで、結果は元の実装と bit 単位で同じ。
   変更: 表を struct から配列にし (2^(i/N) の表 exp2f_tab は 2 つで共有)、
   浮動小数点例外を起こすための式を、同じ値の定数に置き換えた
   (NaN は TCS_NANF を返す)。signaling NaN は区別しない。

   Copyright (c) 2017-2018, Arm Limited.
   SPDX-License-Identifier: MIT */

#include "tcs_math_internal.h"

/*
EXP2F_TABLE_BITS = 5
EXP2F_POLY_ORDER = 3
ULP error: 0.502 (nearest rounding.)
Relative error: 1.69 * 2^-34 in [-ln2/64, ln2/64] (before rounding.)
*/

/* tab[i] = uint(2^(i/N)) - (i << 52-BITS)
   used for computing 2^(k/N) for an int |k| < 150 N as
   double(tab[k%N] + (k << 52-BITS)) */
static const uint64_t exp2f_tab[32] = {
    0x3ff0000000000000u, 0x3fefd9b0d3158574u, 0x3fefb5586cf9890fu,
    0x3fef9301d0125b51u, 0x3fef72b83c7d517bu, 0x3fef54873168b9aau,
    0x3fef387a6e756238u, 0x3fef1e9df51fdee1u, 0x3fef06fe0a31b715u,
    0x3feef1a7373aa9cbu, 0x3feedea64c123422u, 0x3feece086061892du,
    0x3feebfdad5362a27u, 0x3feeb42b569d4f82u, 0x3feeab07dd485429u,
    0x3feea47eb03a5585u, 0x3feea09e667f3bcdu, 0x3fee9f75e8ec5f74u,
    0x3feea11473eb0187u, 0x3feea589994cce13u, 0x3feeace5422aa0dbu,
    0x3feeb737b0cdc5e5u, 0x3feec49182a3f090u, 0x3feed503b23e255du,
    0x3feee89f995ad3adu, 0x3feeff76f2fb5e47u, 0x3fef199bdd85529cu,
    0x3fef3720dcef9069u, 0x3fef5818dcfba487u, 0x3fef7c97337b9b5fu,
    0x3fefa4afa2a490dau, 0x3fefd0765b6e4540u,
};

static const double expf_shift = 0x1.8p+52;
static const double expf_invln2_scaled = 0x1.71547652b82fep+0 * 32;
static const double expf_poly_scaled[] = {
    0x1.c6af84b912394p-5 / 32 / 32 / 32,
    0x1.ebfce50fac4f3p-3 / 32 / 32,
    0x1.62e42ff0c52d6p-1 / 32,
};

static uint32_t expf_top12(float x) { return tcs_asuint(x) >> 20; }

float tcs_expf(float x) {
  uint32_t abstop;
  uint64_t ki, t;
  double kd, xd, z, r, r2, y, s;

  xd = (double)x;
  abstop = expf_top12(x) & 0x7ff;
  if (abstop >= expf_top12(88.0f)) {
    /* |x| >= 88 or x is nan.  */
    if (tcs_asuint(x) == 0xff800000u) /* -inf */
      return 0.0f;
    if (abstop >= expf_top12(TCS_INFF))
      return x + x;
    if (x > 0x1.62e42ep6f) /* x > log(0x1p128) ~= 88.72 */
      return TCS_INFF;
    if (x < -0x1.9fe368p6f) /* x < log(0x1p-150) ~= -103.97 */
      return 0.0f;
  }

  /* x*N/Ln2 = k + r with r in [-1/2, 1/2] and int k.  */
  z = expf_invln2_scaled * xd;

  /* Round and convert z to int, the result is in [-150*N, 128*N] and
     ideally ties-to-even rule is used, otherwise the magnitude of r
     can be bigger which gives larger approximation error.  */
  kd = z + expf_shift;
  ki = tcs_asuint64(kd);
  kd -= expf_shift;
  r = z - kd;

  /* exp(x) = 2^(k/N) * 2^(r/N) ~= s * (C0*r^3 + C1*r^2 + C2*r + 1) */
  t = exp2f_tab[ki % 32];
  t += ki << (52 - 5);
  s = tcs_asdouble(t);
  z = expf_poly_scaled[0] * r + expf_poly_scaled[1];
  r2 = r * r;
  y = expf_poly_scaled[2] * r + 1;
  y = z * r2 + y;
  y = y * s;
  return (float)y;
}


/*
POWF_LOG2_POLY_ORDER = 5
EXP2F_TABLE_BITS = 5

ULP error: 0.82 (~ 0.5 + relerr*2^24)
relerr: 1.27 * 2^-26 (Relative error ~= 128*Ln2*relerr_log2 + relerr_exp2)
relerr_log2: 1.83 * 2^-33 (Relative error of logx.)
relerr_exp2: 1.69 * 2^-34 (Relative error of exp2(ylogx).)
*/

/* tab[i] = {invc, logc} */
static const double powf_log2_tab[16][2] = {
    {0x1.661ec79f8f3bep+0, -0x1.efec65b963019p-2},
    {0x1.571ed4aaf883dp+0, -0x1.b0b6832d4fca4p-2},
    {0x1.49539f0f010bp+0, -0x1.7418b0a1fb77bp-2},
    {0x1.3c995b0b80385p+0, -0x1.39de91a6dcf7bp-2},
    {0x1.30d190c8864a5p+0, -0x1.01d9bf3f2b631p-2},
    {0x1.25e227b0b8eap+0, -0x1.97c1d1b3b7afp-3},
    {0x1.1bb4a4a1a343fp+0, -0x1.2f9e393af3c9fp-3},
    {0x1.12358f08ae5bap+0, -0x1.960cbbf788d5cp-4},
    {0x1.0953f419900a7p+0, -0x1.a6f9db6475fcep-5},
    {0x1p+0, 0x0p+0},
    {0x1.e608cfd9a47acp-1, 0x1.338ca9f24f53dp-4},
    {0x1.ca4b31f026aap-1, 0x1.476a9543891bap-3},
    {0x1.b2036576afce6p-1, 0x1.e840b4ac4e4d2p-3},
    {0x1.9c2d163a1aa2dp-1, 0x1.40645f0c6651cp-2},
    {0x1.886e6037841edp-1, 0x1.88e9c2c1b9ff8p-2},
    {0x1.767dcf5534862p-1, 0x1.ce0a44eb17bccp-2},
};
static const double powf_log2_poly[] = {
    0x1.27616c9496e0bp-2,  -0x1.71969a075c67ap-2, 0x1.ec70a6ca7baddp-2,
    -0x1.7154748bef6c8p-1, 0x1.71547652ab82bp0,
};
static const double powf_exp2_shift_scaled = 0x1.8p+52 / 32;
static const double powf_exp2_poly[] = {
    0x1.c6af84b912394p-5,
    0x1.ebfce50fac4f3p-3,
    0x1.62e42ff0c52d6p-1,
};

/* Subnormal input is normalized so ix has negative biased exponent.  */
static double powf_log2_inline(uint32_t ix) {
  double z, r, r2, r4, p, q, y, y0, invc, logc;
  uint32_t iz, top, tmp;
  int k, i;

  /* x = 2^k z; where z is in range [OFF,2*OFF] and exact.
     The range is split into N subintervals.
     The ith subinterval contains z and c is near its center.  */
  tmp = ix - 0x3f330000;
  i = (tmp >> (23 - 4)) % 16;
  top = tmp & 0xff800000;
  iz = ix - top;
  k = (int32_t)top >> 23; /* arithmetic shift */
  invc = powf_log2_tab[i][0];
  logc = powf_log2_tab[i][1];
  z = (double)tcs_asfloat(iz);

  /* log2(x) = log1p(z/c-1)/ln2 + log2(c) + k */
  r = z * invc - 1;
  y0 = logc + (double)k;

  /* Pipelined polynomial evaluation to approximate log1p(r)/ln2.  */
  r2 = r * r;
  y = powf_log2_poly[0] * r + powf_log2_poly[1];
  p = powf_log2_poly[2] * r + powf_log2_poly[3];
  r4 = r2 * r2;
  q = powf_log2_poly[4] * r + y0;
  q = p * r2 + q;
  y = y * r4 + q;
  return y;
}

static const uint32_t powf_sign_bias = 1 << (5 + 11);

/* The unscaled xd must be in [-1021,1023], sign_bias sets the sign of
   the result.  */
static float powf_exp2_inline(double xd, uint32_t sign_bias) {
  uint64_t ki, ski, t;
  double kd, z, r, r2, y, s;

  /* x = k/N + r with r in [-1/(2N), 1/(2N)] */
  kd = xd + powf_exp2_shift_scaled;
  ki = tcs_asuint64(kd);
  kd -= powf_exp2_shift_scaled; /* k/N */
  r = xd - kd;

  /* exp2(x) = 2^(k/N) * 2^r ~= s * (C0*r^3 + C1*r^2 + C2*r + 1) */
  t = exp2f_tab[ki % 32];
  ski = ki + sign_bias;
  t += ski << (52 - 5);
  s = tcs_asdouble(t);
  z = powf_exp2_poly[0] * r + powf_exp2_poly[1];
  r2 = r * r;
  y = powf_exp2_poly[2] * r + 1;
  y = z * r2 + y;
  y = y * s;
  return (float)y;
}

/* Returns 0 if not int, 1 if odd int, 2 if even int.  The argument is
   the bit representation of a non-zero finite floating-point value.  */
static int powf_checkint(uint32_t iy) {
  int e = iy >> 23 & 0xff;
  if (e < 0x7f)
    return 0;
  if (e > 0x7f + 23)
    return 2;
  if (iy & ((1u << (0x7f + 23 - e)) - 1))
    return 0;
  if (iy & (1u << (0x7f + 23 - e)))
    return 1;
  return 2;
}

static int powf_zeroinfnan(uint32_t ix) {
  return 2 * ix - 1 >= 2u * 0x7f800000 - 1;
}

float tcs_powf(float x, float y) {
  uint32_t sign_bias = 0;
  uint32_t ix, iy;

  ix = tcs_asuint(x);
  iy = tcs_asuint(y);
  if (ix - 0x00800000 >= 0x7f800000 - 0x00800000 || powf_zeroinfnan(iy)) {
    /* Either (x < 0x1p-126 or inf or nan) or (y is 0 or inf or nan).  */
    if (powf_zeroinfnan(iy)) {
      if (2 * iy == 0)
        return 1.0f;
      if (ix == 0x3f800000)
        return 1.0f;
      if (2 * ix > 2u * 0x7f800000 || 2 * iy > 2u * 0x7f800000)
        return x + y;
      if (2 * ix == 2 * 0x3f800000)
        return 1.0f;
      if ((2 * ix < 2 * 0x3f800000) == !(iy & 0x80000000))
        return 0.0f; /* |x|<1 && y==inf or |x|>1 && y==-inf.  */
      return y * y;
    }
    if (powf_zeroinfnan(ix)) {
      float x2 = x * x;
      if (ix & 0x80000000 && powf_checkint(iy) == 1)
        x2 = -x2;
      return iy & 0x80000000 ? 1 / x2 : x2;
    }
    /* x and y are non-zero finite.  */
    if (ix & 0x80000000) {
      /* Finite x < 0.  */
      int yint = powf_checkint(iy);
      if (yint == 0)
        return TCS_NANF;
      if (yint == 1)
        sign_bias = powf_sign_bias;
      ix &= 0x7fffffff;
    }
    if (ix < 0x00800000) {
      /* Normalize subnormal x so exponent becomes negative.  */
      ix = tcs_asuint(x * 0x1p23f);
      ix &= 0x7fffffff;
      ix -= 23 << 23;
    }
  }
  double logx = powf_log2_inline(ix);
  double ylogx = y * logx; /* cannot overflow, y is single prec.  */
  if ((tcs_asuint64(ylogx) >> 47 & 0xffff) >= tcs_asuint64(126.0) >> 47) {
    /* |y*log(x)| >= 126.  */
    if (ylogx > 0x1.fffffffd1d571p+6)
      return sign_bias ? -TCS_INFF : TCS_INFF;
    if (ylogx <= -150.0)
      return sign_bias ? -0.0f : 0.0f;
  }
  return powf_exp2_inline(ylogx, sign_bias);
}
