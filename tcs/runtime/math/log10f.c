/* log10f (正しく丸める)。CORE-MATH (https://core-math.gitlabpages.inria.fr/)
   の src/binary32/log10/log10f.c (commit 0bbf26a) を、GCC / Clang の拡張を
   使わない C に書き直したもの。計算の手順は元のままで、結果は元の実装と
   全入力で bit 単位で同じ。
   変更: __builtin_clz を tcs_clz32 に置き換え、errno と浮動小数点例外の
   設定を外した。

Copyright (c) 2022-2026 Alexei Sibidanov <sibid@uvic.ca>.

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

static float log10_special(float x) {
  uint32_t ux = tcs_asuint(x), ax = ux << 1;
  if (ax == 0u)              /* x = +/-0.0 */
    return tcs_asfloat(0x1ffu << 23); /* -inf */
  if (ux == 0x7f800000u)
    return x; /* x=+inf */
  if (ax > 0xff000000u)
    return x + x; /* x=nan */
  return tcs_asfloat(0x1ff8u << 19); /* x<0: nan */
}

/* reciprocal of 1+i/64 (i=0..64) rounded to 29 bits */
static const double log10_tr[] = {
    0x1p+0,         0x1.f81f82p-1,  0x1.f07c1fp-1,  0x1.e9131acp-1,
    0x1.e1e1e1ep-1, 0x1.dae6077p-1, 0x1.d41d41dp-1, 0x1.cd85689p-1,
    0x1.c71c71cp-1, 0x1.c0e0704p-1, 0x1.bacf915p-1, 0x1.b4e81b5p-1,
    0x1.af286bdp-1, 0x1.a98ef6p-1,  0x1.a41a41ap-1, 0x1.9ec8e95p-1,
    0x1.999999ap-1, 0x1.948b0fdp-1, 0x1.8f9c19p-1,  0x1.8acb90fp-1,
    0x1.8618618p-1, 0x1.8181818p-1, 0x1.7d05f41p-1, 0x1.78a4c81p-1,
    0x1.745d174p-1, 0x1.702e05cp-1, 0x1.6c16c17p-1, 0x1.6816817p-1,
    0x1.642c859p-1, 0x1.605816p-1,  0x1.5c9882cp-1, 0x1.58ed231p-1,
    0x1.5555555p-1, 0x1.51d07ebp-1, 0x1.4e5e0a7p-1, 0x1.4afd6ap-1,
    0x1.47ae148p-1, 0x1.446f865p-1, 0x1.4141414p-1, 0x1.3e22cbdp-1,
    0x1.3b13b14p-1, 0x1.3813814p-1, 0x1.3521cfbp-1, 0x1.323e34ap-1,
    0x1.2f684bep-1, 0x1.2c9fb4ep-1, 0x1.29e412ap-1, 0x1.27350b9p-1,
    0x1.2492492p-1, 0x1.21fb781p-1, 0x1.1f7047ep-1, 0x1.1cf06aep-1,
    0x1.1a7b961p-1, 0x1.1811812p-1, 0x1.15b1e5fp-1, 0x1.135c811p-1,
    0x1.1111111p-1, 0x1.0ecf56cp-1, 0x1.0c9715p-1,  0x1.0a6810ap-1,
    0x1.0842108p-1, 0x1.0624dd3p-1, 0x1.041041p-1,  0x1.0204081p-1,
    0.5};
/* logarithms of the reciprocals with offset */
static const double log10_tl[] = {
    -0x1.2p-46,           0x1.b947689310dfap-8, 0x1.b5e909c96d11bp-7,
    0x1.45f4f59ed1e08p-6, 0x1.af5f92cbd8bc1p-6, 0x1.0ba01a606dcdep-5,
    0x1.3ed119b9a29cdp-5, 0x1.714834298ed14p-5, 0x1.a30a9d983564cp-5,
    0x1.d41d512670665p-5, 0x1.02428c0f65442p-4, 0x1.1a23444eecb67p-4,
    0x1.31b30543f4bddp-4, 0x1.48f3ed39bfc2dp-4, 0x1.5fe8049a0e34bp-4,
    0x1.769140a6a9f3p-4,  0x1.8cf1836c98bdbp-4, 0x1.a30a9d55540cap-4,
    0x1.b8de4d1ee8167p-4, 0x1.ce6e4202ca20fp-4, 0x1.e3bc1accacd3p-4,
    0x1.f8c9683b5aafdp-4, 0x1.06cbd68ca9a03p-3, 0x1.11142f19df6c5p-3,
    0x1.1b3e71fa7a913p-3, 0x1.254b4d37a4677p-3, 0x1.2f3b6912cbe9cp-3,
    0x1.390f68311581ap-3, 0x1.42c7e7fffc53dp-3, 0x1.4c65808c78cd1p-3,
    0x1.55e8c50751beap-3, 0x1.5f52445dec36cp-3, 0x1.68a288c3f1195p-3,
    0x1.71da17bdf0cadp-3, 0x1.7af973608af6ep-3, 0x1.84011952a250ep-3,
    0x1.8cf1837a7e9f4p-3, 0x1.95cb2891e436ap-3, 0x1.9e8e7b0f86974p-3,
    0x1.a73beaa5db121p-3, 0x1.afd3e39455867p-3, 0x1.b856cf060d985p-3,
    0x1.c0c5134de1f9p-3,  0x1.c91f1371bc934p-3, 0x1.d1652ffcd3ee8p-3,
    0x1.d997c6f635e09p-3, 0x1.e1b733ab90edp-3,  0x1.e9c3ceadac7ebp-3,
    0x1.f1bdeec43a29ap-3, 0x1.f9a5e7a5fa392p-3, 0x1.00be05ac02ef5p-2,
    0x1.04a054d81a29ep-2, 0x1.087a0835957c5p-2, 0x1.0c4b4570994e1p-2,
    0x1.101431aa1fe1bp-2, 0x1.13d4f08b98da2p-2, 0x1.178da53edb85cp-2,
    0x1.1b3e71e9f9d22p-2, 0x1.1ee777defdeb7p-2, 0x1.2288d7b48e205p-2,
    0x1.2622b0f52e469p-2, 0x1.29b522a4c62dep-2, 0x1.2d404b0e30f4ap-2,
    0x1.30c4478f3fbafp-2, 0x1.34413509f78dfp-2};
/* 10^n */
static const float log10_st[] = {0x1.2a05f2p+33f, 0x1.4p+3f,     0x1.9p+6f,     0,
                           0x1.f4p+9f,      0,             0x1.388p+13f,  0x1.86ap+16f,
                           0,               0x1.e848p+19f, 0,             0x1.312dp+23f,
                           0x1.7d784p+26f,  0,             0x1.dcd65p+29f, 0x1p+0f};
static const double log10_b[] = {0x1.bcb7b15d35067p-2, -0x1.bcbb1cd29cbafp-3,
                           0x1.2870e2624ce4ep-3};
static const double log10_c[] = {0x1.bcb7b1526e50ep-2, -0x1.bcb7b1526e53dp-3,
                           0x1.287a7636f3fa2p-3, -0x1.bcb7b146a14b3p-4,
                           0x1.63c627d5219cbp-4, -0x1.2880736c8762dp-4,
                           0x1.fc1ecf913961ap-5};

float tcs_log10f(float x) {
  /* ln(2)/ln(10) */
  const double ln10 = 0x1.34413509f79ffp-2, ln10h = 0x1.34413509f7ap-2,
               ln10l = -0x1.0cee0ed4ca7e9p-54;
  uint32_t ux = tcs_asuint(x);
  if (ux >= 0x7f800000u)
    return log10_special(x); /* <=-0, nan, inf */
  if (ux == tcs_asuint(log10_st[(ux >> 24) & 0xf])) { /* x = 10^n */
    unsigned je = (unsigned)(((int)ux >> 23) - 126);
    je = (je * 0x4d104d4u) >> 28;
    return (float)je;
  }
  if (ux < 0x00800000u) {
    if (ux == 0u)
      return log10_special(x); /* x=+0 */
    /* subnormal */
    int n = tcs_clz32(ux) - 8;
    ux <<= n;
    ux -= (uint32_t)n << 23;
  }
  int e = ((int)ux >> 23) - 127;
  int64_t m = ux & ((1 << 23) - 1), j = (m + (1 << (23 - 7))) >> (23 - 6);
  double z = tcs_asdouble(((uint64_t)m << (52 - 23)) | (0x3ffull << 52)) * log10_tr[j] - 1,
         z2 = z * z;
  double r = ((e * ln10 + log10_tl[j]) + z * log10_b[0]) + z2 * (log10_b[1] + z * log10_b[2]);
  float ub = (float)r, lb = (float)(r + 0x1.af23fp-34);
  if (ub != lb) {
    double f = z * ((log10_c[0] + z * log10_c[1]) +
                    z2 * ((log10_c[2] + z * log10_c[3]) + z2 * (log10_c[4] + z * log10_c[5] + z2 * log10_c[6])));
    f += ln10l * e;
    f += log10_tl[j] - log10_tl[0];
    double el = e * ln10h;
    r = el + f;
    ub = (float)r;
    if (!(tcs_asuint64(r) & ((1 << 28) - 1))) {
      double dr = (el - r) + f;
      r += dr * 32;
      ub = (float)r;
    }
  }
  return ub;
}
