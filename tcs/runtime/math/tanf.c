/* tanf (正しく丸める)。CORE-MATH (https://core-math.gitlabpages.inria.fr/)
   の src/binary32/tan/tanf.c (commit 0bbf26a) を、GCC / Clang の拡張を使わない
   C に書き直したもの。計算の手順は元のままで、結果は元の実装と全入力で
   bit 単位で同じ。
   変更: __int128 の積を tcs_mul64 に、__builtin_roundeven を
   tcs_roundeven_small に、__builtin_fmaf を tcs_fmaf に置き換え、errno と
   浮動小数点例外の設定を外した (NaN は TCS_NANF を返す)。

Copyright (c) 2022-2025 Alexei Sibidanov.

This file is part of the CORE-MATH project
(https://core-math.gitlabpages.inria.fr/).

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

#include "tcs_math_internal.h"

/* argument reduction
   for |z| < 2^28, return r such that 2/pi*x = q + r */
static double tan_rltl(float z, int *q) {
  double x = z;
  /* The constants in idh, idl approximate 2/pi. Since idh is representable
     on 28 bits, and x on 24 bits, idh is exact */
  double idl = -0x1.b1bbead603d8bp-32 * x, idh = 0x1.45f306ep-1 * x,
         id = tcs_roundeven_small(idh);
  *q = (int)(int64_t)id;
  return (idh - id) + idl;
}

/* argument reduction, same as rltl, but for |x| >= 2^28 */
static double tan_rbig(uint32_t u, int *q) {
  static const uint64_t ipi[] = {0xfe5163abdebbc562u, 0xdb6295993c439041u,
                                 0xfc2757d1f534ddc0u, 0xa2f9836e4e441529u};
  int e = (u >> 23) & 0xff, i;
  uint64_t m = (u & (~0u >> 9)) | 1 << 23;
  uint64_t p0h, p1h, p2h, p3h;
  (void)tcs_mul64(m, ipi[0], &p0h);
  uint64_t p1l = tcs_mul64(m, ipi[1], &p1h);
  p1l += p0h;
  p1h += p1l < p0h;
  uint64_t p2l = tcs_mul64(m, ipi[2], &p2h);
  p2l += p1h;
  p2h += p2l < p1h;
  uint64_t p3l = tcs_mul64(m, ipi[3], &p3h);
  p3l += p2h;
  p3h += p3l < p2h;
  int64_t a;
  int k = e - 127, s = k - 23;
  /* rbig() is called in the case 127+28 <= e < 0xff
     thus 155 <= e <= 254, which yields 28 <= k <= 127 and 5 <= s <= 104 */
  if (s < 64) {
    i = (int32_t)(uint32_t)(p3h << s | p3l >> (64 - s));
    a = (int64_t)(p3l << s | p2l >> (64 - s));
  } else if (s == 64) {
    i = (int32_t)(uint32_t)p3l;
    a = (int64_t)p2l;
  } else { /* s > 64 */
    i = (int32_t)(uint32_t)(p3l << (s - 64) | p2l >> (128 - s));
    a = (int64_t)(p2l << (s - 64) | p1l >> (128 - s));
  }
  int32_t sgn = -(int32_t)(u >> 31);
  int64_t sm = -(int64_t)((uint64_t)a >> 63);
  i = (int)(i - sm);
  double z = (double)(a ^ (int64_t)sgn) * 0x1p-64;
  i = (i ^ sgn) - sgn;
  *q = i;
  return z;
}

static const double tan_cn[] = {0x1.921fb54442d18p+0, -0x1.fd226e573289fp-2,
                            0x1.b7a60c8dac9f6p-6, -0x1.725beb40f33e5p-13};
static const double tan_cd[] = {0x1p+0, -0x1.2395347fb829dp+0,
                            0x1.2313660f29c36p-3, -0x1.9a707ab98d1c1p-9};
static const double tan_sw[] = {0, 1};

/* 有理式の近似で丸めを誤る入力と、その正しい値 (rh + rl) */
static const float tan_arg[] = {0x1.143ec4p+0f,  0x1.ada6aap+27f, 0x1.af61dap+48f,
                               0x1.0088bcp+52f, 0x1.f90dfcp+72f, 0x1.cc4e22p+85f,
                               0x1.a6ce12p+86f, 0x1.6a0b76p+102f};
static const float tan_rh[] = {0x1.ddf9f6p+0f,  0x1.e80304p-3f,  0x1.60d1c8p-2f,
                              0x1.ca1edp+0f,   0x1.597f9cp-1f,  -0x1.f33584p+1f,
                              -0x1.c5612ep-1f, -0x1.e42a1ep+0f};
static const float tan_rl[] = {-0x1.891d24p-52f, 0x1.419f46p-58f, -0x1.2d6c3ap-55f,
                              0x1.f6053p-53f,   0x1.925978p-53f, 0x1.d7254ap-51f,
                              -0x1.26c33ep-53f, -0x1.1dc906p-52f};

float tcs_tanf(float x) {
  uint32_t u = tcs_asuint(x);
  int e = (u >> 23) & 0xff, i;
  double z;
  if (e < 127 + 28) { /* |x| < 2^28 */
    if (e < 115) {    /* |x| < 2^-13 */
      if (e < 102)    /* |x| < 2^-26 */
        return tcs_fmaf(x, tcs_asfloat(u & 0x7fffffffu), x);
      float x2 = x * x;
      return tcs_fmaf(x, 0x1.555556p-2f * x2, x);
    }
    z = tan_rltl(x, &i);
  } else if (e < 0xff) {
    z = tan_rbig(u, &i);
  } else {
    if (u << 9)
      return x + x; /* nan */
    return TCS_NANF;  /* inf */
  }
  double z2 = z * z, z4 = z2 * z2;
  double n = tan_cn[0] + z2 * tan_cn[1], n2 = tan_cn[2] + z2 * tan_cn[3];
  n += z4 * n2;
  double d = tan_cd[0] + z2 * tan_cd[1], d2 = tan_cd[2] + z2 * tan_cd[3];
  d += z4 * d2;
  n *= z;
  double s0 = tan_sw[i & 1], s1 = tan_sw[1 - (i & 1)];
  double r1 = (n * s1 - d * s0) / (n * s0 + d * s1);
  uint64_t tail = (tcs_asuint64(r1) + 7) & (~(uint64_t)0 >> 35);
  if (tail <= 14) {
    uint32_t ax = u & (~0u >> 1), sgn = u >> 31;
    for (int j = 0; j < 8; j++) {
      if (tcs_asuint(tan_arg[j]) == ax) {
        if (sgn)
          return -tan_rh[j] - tan_rl[j];
        else
          return tan_rh[j] + tan_rl[j];
      }
    }
  }
  return (float)r1;
}
