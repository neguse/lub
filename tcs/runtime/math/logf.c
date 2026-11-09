/* logf。musl 1.2.6 の src/math/logf.c と logf_data.c (Arm の
   optimized-routines 由来) を、runtime/math の書き方に起こし直したもの。
   計算の手順は元のままで、結果は元の実装と全入力で bit 単位で同じ。
   変更: 表を struct から配列にし、浮動小数点例外を起こすための式を、
   同じ値の定数に置き換えた (NaN は TCS_NANF を返す)。

   Copyright (c) 2017-2018, Arm Limited.
   SPDX-License-Identifier: MIT */

#include "tcs_math_internal.h"

/*
LOGF_TABLE_BITS = 4
LOGF_POLY_ORDER = 4
ULP error: 0.818 (nearest rounding.)
Relative error: 1.957 * 2^-26 (before rounding.)
*/

/* tab[i] = {invc, logc} */
static const double logf_tab[16][2] = {
    {0x1.661ec79f8f3bep+0, -0x1.57bf7808caadep-2},
    {0x1.571ed4aaf883dp+0, -0x1.2bef0a7c06ddbp-2},
    {0x1.49539f0f010bp+0, -0x1.01eae7f513a67p-2},
    {0x1.3c995b0b80385p+0, -0x1.b31d8a68224e9p-3},
    {0x1.30d190c8864a5p+0, -0x1.6574f0ac07758p-3},
    {0x1.25e227b0b8eap+0, -0x1.1aa2bc79c81p-3},
    {0x1.1bb4a4a1a343fp+0, -0x1.a4e76ce8c0e5ep-4},
    {0x1.12358f08ae5bap+0, -0x1.1973c5a611cccp-4},
    {0x1.0953f419900a7p+0, -0x1.252f438e10c1ep-5},
    {0x1p+0, 0x0p+0},
    {0x1.e608cfd9a47acp-1, 0x1.aa5aa5df25984p-5},
    {0x1.ca4b31f026aap-1, 0x1.c5e53aa362eb4p-4},
    {0x1.b2036576afce6p-1, 0x1.526e57720db08p-3},
    {0x1.9c2d163a1aa2dp-1, 0x1.bc2860d22477p-3},
    {0x1.886e6037841edp-1, 0x1.1058bc8a07ee1p-2},
    {0x1.767dcf5534862p-1, 0x1.4043057b6ee09p-2},
};
static const double logf_ln2 = 0x1.62e42fefa39efp-1;
/* First order coefficient is 1.  */
static const double logf_poly[] = {
    -0x1.00ea348b88334p-2,
    0x1.5575b0be00b6ap-2,
    -0x1.ffffef20a4123p-2,
};

float tcs_logf(float x) {
  double z, r, r2, y, y0, invc, logc;
  uint32_t ix, iz, tmp;
  int k, i;

  ix = tcs_asuint(x);
  /* Fix sign of zero with downward rounding when x==1.  */
  if (ix == 0x3f800000)
    return 0;
  if (ix - 0x00800000 >= 0x7f800000 - 0x00800000) {
    /* x < 0x1p-126 or inf or nan.  */
    if (ix * 2 == 0)
      return -TCS_INFF;
    if (ix == 0x7f800000) /* log(inf) == inf.  */
      return x;
    if ((ix & 0x80000000) || ix * 2 >= 0xff000000)
      return TCS_NANF;
    /* x is subnormal, normalize it.  */
    ix = tcs_asuint(x * 0x1p23f);
    ix -= 23 << 23;
  }

  /* x = 2^k z; where z is in range [OFF,2*OFF] and exact.
     The range is split into N subintervals.
     The ith subinterval contains z and c is near its center.  */
  tmp = ix - 0x3f330000;
  i = (tmp >> (23 - 4)) % 16;
  k = (int32_t)tmp >> 23; /* arithmetic shift */
  iz = ix - (tmp & 0xff800000);
  invc = logf_tab[i][0];
  logc = logf_tab[i][1];
  z = (double)tcs_asfloat(iz);

  /* log(x) = log1p(z/c-1) + log(c) + k*Ln2 */
  r = z * invc - 1;
  y0 = logc + (double)k * logf_ln2;

  /* Pipelined polynomial evaluation to approximate log1p(r).  */
  r2 = r * r;
  y = logf_poly[1] * r + logf_poly[2];
  y = logf_poly[0] * r2 + y;
  y = y * r2 + (y0 + r);
  return (float)y;
}
