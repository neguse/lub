/* log2f。musl 1.2.6 の src/math/log2f.c と log2f_data.c (Arm の
   optimized-routines 由来) を、runtime/math の書き方に起こし直したもの。
   計算の手順は元のままで、結果は元の実装と全入力で bit 単位で同じ。
   変更: 表を struct から配列にし、浮動小数点例外を起こすための式を、
   同じ値の定数に置き換えた (NaN は TCS_NANF を返す)。

   Copyright (c) 2017-2018, Arm Limited.
   SPDX-License-Identifier: MIT */

#include "tcs_math_internal.h"

/*
LOG2F_TABLE_BITS = 4
LOG2F_POLY_ORDER = 4
ULP error: 0.752 (nearest rounding.)
Relative error: 1.9 * 2^-26 (before rounding.)
*/

/* tab[i] = {invc, logc} */
static const double log2f_tab[16][2] = {
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
static const double log2f_poly[] = {
    -0x1.712b6f70a7e4dp-2,
    0x1.ecabf496832ep-2,
    -0x1.715479ffae3dep-1,
    0x1.715475f35c8b8p0,
};

float tcs_log2f(float x) {
  double z, r, r2, p, y, y0, invc, logc;
  uint32_t ix, iz, top, tmp;
  int k, i;

  ix = tcs_asuint(x);
  /* Fix sign of zero with downward rounding when x==1.  */
  if (ix == 0x3f800000)
    return 0;
  if (ix - 0x00800000 >= 0x7f800000 - 0x00800000) {
    /* x < 0x1p-126 or inf or nan.  */
    if (ix * 2 == 0)
      return -TCS_INFF;
    if (ix == 0x7f800000) /* log2(inf) == inf.  */
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
  top = tmp & 0xff800000;
  iz = ix - top;
  k = (int32_t)tmp >> 23; /* arithmetic shift */
  invc = log2f_tab[i][0];
  logc = log2f_tab[i][1];
  z = (double)tcs_asfloat(iz);

  /* log2(x) = log1p(z/c-1)/ln2 + log2(c) + k */
  r = z * invc - 1;
  y0 = logc + (double)k;

  /* Pipelined polynomial evaluation to approximate log1p(r)/ln2.  */
  r2 = r * r;
  y = log2f_poly[1] * r + log2f_poly[2];
  y = log2f_poly[0] * r2 + y;
  p = log2f_poly[3] * r + y0;
  y = y * r2 + p;
  return (float)y;
}
