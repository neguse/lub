/* sinf / cosf (正しく丸める)。CORE-MATH (https://core-math.gitlabpages.inria.fr/)
   の src/binary32/sin/sinf.c と src/binary32/cos/cosf.c (commit 0bbf26a) を、
   GCC / Clang の拡張を使わない C に書き直したもの。計算の手順は元のままで、
   結果は元の実装と全入力で bit 単位で同じ。
   変更: __int128 の積を tcs_mul64 に、__builtin_roundeven を
   tcs_roundeven_small に、__builtin_fmaf を tcs_fmaf に置き換え、errno と
   浮動小数点例外の設定を外した (NaN は TCS_NANF を返す)。2 つのファイルで
   同じだった引数の縮約と多項式の係数は 1 つにまとめた。

Copyright (c) 2022-2025 Alexei Sibidanov.
Copyright (c) 2022-2023 Alexei Sibidanov.

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

/* |x| >= 2^28 の引数の縮約: x * 4/pi = q + z (z は [-1/2, 1/2])。
   2/pi の 256 bit と仮数の積から、q と z の bit を取り出す */
static double sincos_rbig(uint32_t u, int *q) {
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
  int k = e - 124, s = k - 23;
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

static double sincos_rltl(float z, int *q) {
  double x = z;
  double idl = -0x1.b1bbead603d8bp-29 * x, idh = 0x1.45f306ep+2 * x,
         id = tcs_roundeven_small(idh);
  *q = (int32_t)(uint32_t)tcs_asuint64(0x1.8p52 + id);
  return (idh - id) + idl;
}

static double sincos_rltl0(double x, int *q) {
  double idh = 0x1.45f306dc9c883p+2 * x, id = tcs_roundeven_small(idh);
  *q = (int32_t)(uint32_t)tcs_asuint64(0x1.8p52 + id);
  return idh - id;
}

static const double sincos_b[] = {0x1.3bd3cc9be45dcp-6, -0x1.03c1f081b0833p-14,
                           0x1.55d3c6fc9ac1fp-24, -0x1.e1d3ff281b40dp-35};
static const double sincos_a[] = {0x1.921fb54442d17p-3, -0x1.4abbce6256a39p-10,
                           0x1.466bc5a518c16p-19, -0x1.32bdc61074ff6p-29};
/* sin(i * pi/16)。cos の表 (tbc) は 8 つずらした cos(i * pi/16) */
static const double sincos_tb[] = {
    0x0p+0,                 0x1.8f8b83c69a60bp-3,   0x1.87de2a6aea963p-2,
    0x1.1c73b39ae68c8p-1,   0x1.6a09e667f3bcdp-1,   0x1.a9b66290ea1a3p-1,
    0x1.d906bcf328d46p-1,   0x1.f6297cff75cbp-1,    0x1p+0,
    0x1.f6297cff75cbp-1,    0x1.d906bcf328d46p-1,   0x1.a9b66290ea1a3p-1,
    0x1.6a09e667f3bcdp-1,   0x1.1c73b39ae68c8p-1,   0x1.87de2a6aea963p-2,
    0x1.8f8b83c69a60bp-3,   0x0p+0,                 -0x1.8f8b83c69a60bp-3,
    -0x1.87de2a6aea963p-2,  -0x1.1c73b39ae68c8p-1,  -0x1.6a09e667f3bcdp-1,
    -0x1.a9b66290ea1a3p-1,  -0x1.d906bcf328d46p-1,  -0x1.f6297cff75cbp-1,
    -0x1p+0,                -0x1.f6297cff75cbp-1,   -0x1.d906bcf328d46p-1,
    -0x1.a9b66290ea1a3p-1,  -0x1.6a09e667f3bcdp-1,  -0x1.1c73b39ae68c8p-1,
    -0x1.87de2a6aea963p-2,  -0x1.8f8b83c69a60bp-3};

static const double sincos_tbc[] = {
    0x1p+0,                 0x1.f6297cff75cbp-1,    0x1.d906bcf328d46p-1,
    0x1.a9b66290ea1a3p-1,   0x1.6a09e667f3bcdp-1,   0x1.1c73b39ae68c8p-1,
    0x1.87de2a6aea963p-2,   0x1.8f8b83c69a60bp-3,   0x0p+0,
    -0x1.8f8b83c69a60bp-3,  -0x1.87de2a6aea963p-2,  -0x1.1c73b39ae68c8p-1,
    -0x1.6a09e667f3bcdp-1,  -0x1.a9b66290ea1a3p-1,  -0x1.d906bcf328d46p-1,
    -0x1.f6297cff75cbp-1,   -0x1p+0,                -0x1.f6297cff75cbp-1,
    -0x1.d906bcf328d46p-1,  -0x1.a9b66290ea1a3p-1,  -0x1.6a09e667f3bcdp-1,
    -0x1.1c73b39ae68c8p-1,  -0x1.87de2a6aea963p-2,  -0x1.8f8b83c69a60bp-3,
    0x0p+0,                 0x1.8f8b83c69a60bp-3,   0x1.87de2a6aea963p-2,
    0x1.1c73b39ae68c8p-1,   0x1.6a09e667f3bcdp-1,   0x1.a9b66290ea1a3p-1,
    0x1.d906bcf328d46p-1,   0x1.f6297cff75cbp-1};

/* 多項式の近似で丸めを誤る入力と、その正しい値 (rh + rl) */
static const float sin_arg[] = {0x1.33333p+13f, 0x1.75b8a2p-1f,
                                0x1.4f0654p+0f, 0x1.2d97c8p+3f};
static const float sin_rh[] = {-0x1.63f4bap-2f, 0x1.55688ap-1f,
                               0x1.ee836cp-1f, -0x1.99bc5ap-26f};
static const float sin_rl[] = {-0x1p-27f, -0x1p-26f, -0x1p-26f, -0x1p-51f};

static float as_sinf_database(float x, double r) {
  uint32_t ax = tcs_asuint(x) & (~0u >> 1);
  for (int i = 0; i < 4; i++)
    if (tcs_asuint(sin_arg[i]) == ax) {
      float sgn = (tcs_asuint(x) >> 31) ? -1.0f : 1.0f;
      return sgn * sin_rh[i] + sgn * sin_rl[i];
    }
  return (float)r;
}

static float as_sinf_big(float x) {
  uint32_t u = tcs_asuint(x);
  uint32_t ax = u << 1;
  if (ax >= 0xffu << 24) { /* nan or +-inf */
    if (ax << 8)
      return x + x; /* nan */
    return TCS_NANF;
  }
  int ia;
  double z = sincos_rbig(u, &ia);
  double z2 = z * z, z4 = z2 * z2;
  double aa = (sincos_a[0] + z2 * sincos_a[1]) + z4 * (sincos_a[2] + z2 * sincos_a[3]);
  double bb = (sincos_b[0] + z2 * sincos_b[1]) + z4 * (sincos_b[2] + z2 * sincos_b[3]);
  double s0 = sincos_tb[ia & 31], c0 = sincos_tb[(ia + 8u) & 31];
  double r = s0 + z * (aa * c0 - bb * (z * s0));
  return (float)r;
}

float tcs_sinf(float x) {
  uint32_t ax = tcs_asuint(x) << 1;
  int ia;
  double z0 = x, z;
  if (ax > 0x99000000u || ax < 0x73000000u) {
    /* |x| > 0x1p+26 or |x| < 0x1p-12 */
    if (ax < 0x73000000u) {   /* |x| < 0x1p-12 */
      if (ax < 0x66000000u) { /* |x| < 0x1p-25 */
        if (ax == 0u)
          return x;
        return tcs_fmaf(-x, tcs_asfloat(tcs_asuint(x) & 0x7fffffffu), x);
      }
      return (-0x1.555556p-3f * x) * (x * x) + x;
    }
    return as_sinf_big(x);
  }
  if (ax < 0x822d97c8u) {
    if (ax == 0x7e75b8a2u || ax == 0x7f4f0654u)
      return as_sinf_database(x, 0.0);
    z = sincos_rltl0(z0, &ia);
  } else {
    if (ax == 0x8c333330u)
      return as_sinf_database(x, 0.0);
    z = sincos_rltl(x, &ia);
  }
  double z2 = z * z, z4 = z2 * z2;
  double aa = (sincos_a[0] + z2 * sincos_a[1]) + z4 * (sincos_a[2] + z2 * sincos_a[3]);
  double bb = (sincos_b[0] + z2 * sincos_b[1]) + z4 * (sincos_b[2] + z2 * sincos_b[3]);
  double s0 = sincos_tb[ia & 31], c0 = sincos_tb[(ia + 8) & 31];
  double r = s0 + aa * (z * c0) - bb * (z2 * s0);
  return (float)r;
}

static const float cos_arg[] = {0x1.2d97c8p+2f, 0x1.4555p+51f, 0x1.48a858p+54f,
                                0x1.3170fp+63f, 0x1.2b9622p+67f};
static const float cos_rh[] = {0x1.99bc5cp-27f, 0x1.115d7ep-1f, 0x1.f48148p-2f,
                               0x1.fe2976p-1f, 0x1.f0285ep-1f};
static const float cos_rl[] = {-0x1p-52f, -0x1p-26f, 0x1p-27f, 0x1p-26f,
                               -0x1p-26f};

static float as_cosf_database(float x, double r) {
  uint32_t ax = tcs_asuint(x) & (~0u >> 1);
  for (int i = 0; i < 5; i++)
    if (tcs_asuint(cos_arg[i]) == ax)
      return cos_rh[i] + cos_rl[i];
  return (float)r;
}

static float as_cosf_big(float x) {
  uint32_t u = tcs_asuint(x);
  uint32_t ax = u << 1;
  if (ax >= 0xffu << 24) { /* nan or +-inf */
    if (ax << 8)
      return x + x; /* nan */
    return TCS_NANF;
  }
  int ia;
  double z = sincos_rbig(u, &ia);
  double z2 = z * z, z4 = z2 * z2;
  double aa = (sincos_a[0] + z2 * sincos_a[1]) + z4 * (sincos_a[2] + z2 * sincos_a[3]);
  double bb = (sincos_b[0] + z2 * sincos_b[1]) + z4 * (sincos_b[2] + z2 * sincos_b[3]);
  double s0 = sincos_tbc[(ia + 8u) & 31], c0 = sincos_tbc[ia & 31];
  double r = c0 + z * (aa * s0 - bb * (z * c0));
  uint64_t tail = (tcs_asuint64(r) + 6) & (~(uint64_t)0 >> 36);
  if (tail <= 12)
    return as_cosf_database(x, r);
  return (float)r;
}

float tcs_cosf(float x) {
  uint32_t ax = tcs_asuint(x) << 1;
  int ia;
  double z0 = x, z;
  if (ax > 0x99000000u || ax < 0x74000000u) {
    /* |x| > 0x1p+26 or |x| < 0x1p-11 */
    if (ax < 0x74000000u) /* |x| < 0x1p-11 */
      /* For |x| < 2^-11, cos(x) rounds to the same value as 1-x^2/2
         for all rounding modes. */
      return 0.5f * tcs_fmaf(x, -x, 2.0f);
    return as_cosf_big(x); /* |x| > 0x1p+26 */
  }
  if (ax < 0x82a41896u) {
    if (ax == 0x812d97c8u)
      return as_cosf_database(x, 0.0);
    z = sincos_rltl0(z0, &ia);
  } else {
    z = sincos_rltl(x, &ia);
  }
  double z2 = z * z, z4 = z2 * z2;
  double aa = (sincos_a[0] + z2 * sincos_a[1]) + z4 * (sincos_a[2] + z2 * sincos_a[3]);
  double bb = (sincos_b[0] + z2 * sincos_b[1]) + z4 * (sincos_b[2] + z2 * sincos_b[3]);
  double c0 = sincos_tbc[ia & 31], s0 = sincos_tbc[(ia + 8) & 31];
  double r = c0 + aa * (z * s0) - bb * (z2 * c0);
  return (float)r;
}
