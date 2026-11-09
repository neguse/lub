/* runtime/math の実装が共有する helper。GCC / Clang の拡張 (__int128、
   __builtin_*) を使わず、MSVC でも同じ結果になる形だけで書く。
   C# 版 (TinySystem/TcsMath.Util.cs) は同じ形を写す。 */
#ifndef TCS_MATH_INTERNAL_H
#define TCS_MATH_INTERNAL_H

#include <float.h>
#include <stdint.h>
#include <string.h>

#include "tcs_math.h"

#if FLT_EVAL_METHOD != 0
#error "runtime/math requires FLT_EVAL_METHOD == 0"
#endif
#if defined(__FAST_MATH__)
#error "runtime/math must not be built with fast-math"
#endif

static inline uint32_t tcs_asuint(float f) {
  uint32_t u;
  memcpy(&u, &f, sizeof u);
  return u;
}

static inline float tcs_asfloat(uint32_t u) {
  float f;
  memcpy(&f, &u, sizeof f);
  return f;
}

static inline uint64_t tcs_asuint64(double f) {
  uint64_t u;
  memcpy(&u, &f, sizeof u);
  return u;
}

static inline double tcs_asdouble(uint64_t u) {
  double f;
  memcpy(&f, &u, sizeof f);
  return f;
}

/* 結果に bit の決まった値を返す (0.0f / 0.0f などは CPU で NaN の符号が違う) */
#define TCS_NANF tcs_asfloat(0x7fc00000u)
#define TCS_INFF tcs_asfloat(0x7f800000u)

/* |x| < 2^51 の x を、偶数への丸めで整数にする (round-to-nearest の加算の丸めを使う) */
static inline double tcs_roundeven_small(double x) {
  const double shift = 0x1.8p52;
  return (x + shift) - shift;
}

/* 64 bit 同士の積の上位と下位 (unsigned __int128 の積の代わり) */
static inline uint64_t tcs_mul64(uint64_t a, uint64_t b, uint64_t *hi) {
  uint64_t al = (uint32_t)a, ah = a >> 32, bl = (uint32_t)b, bh = b >> 32;
  uint64_t ll = al * bl, lh = al * bh, hl = ah * bl, hh = ah * bh;
  uint64_t mid = (ll >> 32) + (uint32_t)lh + (uint32_t)hl;
  *hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
  return (mid << 32) | (uint32_t)ll;
}

static inline int tcs_clz32(uint32_t x) {
  int n = 0;
  if (!(x & 0xffff0000u)) { n += 16; x <<= 16; }
  if (!(x & 0xff000000u)) { n += 8; x <<= 8; }
  if (!(x & 0xf0000000u)) { n += 4; x <<= 4; }
  if (!(x & 0xc0000000u)) { n += 2; x <<= 2; }
  if (!(x & 0x80000000u)) { n += 1; }
  return n;
}

/* fmaf(a, b, c) を double の演算だけで正しく丸める。a * b は double で正確で、
   和の誤差を TwoSum で求め、double へは奇数への丸め (round to odd) で寄せて
   から float にする (double の精度は float の 24 + 2 bit 以上あるので、
   2 回の丸めでも正しく丸めた結果と同じになる)。overflow / underflow は起きない */
static inline float tcs_fmaf(float a, float b, float c) {
  double p = (double)a * (double)b;
  double dc = c;
  double s = p + dc;
  double bb = s - p;
  double e = (p - (s - bb)) + (dc - bb);
  if (e != 0) {
    uint64_t u = tcs_asuint64(s);
    if ((u & 1) == 0)
      u += ((e > 0) == (s > 0)) ? 1 : (uint64_t)-1;
    s = tcs_asdouble(u);
  }
  return (float)s;
}

/* a * b を double に丸めた p に対して、a * b - p を正確に返す
   (fma(a, b, -p) の代わり。Veltkamp の分割と Dekker の積。
   使う所の値の範囲では overflow / underflow しない) */
static inline double tcs_split_hi(double a) {
  double c = 134217729.0 * a; /* 2^27 + 1 */
  return c - (c - a);
}

static inline double tcs_mul_err(double a, double b, double p) {
  double ah = tcs_split_hi(a), al = a - ah;
  double bh = tcs_split_hi(b), bl = b - bh;
  return (((ah * bh - p) + ah * bl) + al * bh) + al * bl;
}

/* q = y / x を double に丸めた q に対して、y - q * x を正確に返す
   (fma(-q, x, y) の代わり。この剰余は double で正確に表せる) */
static inline double tcs_div_rem(double y, double x, double q) {
  double p = q * x;
  return (y - p) - tcs_mul_err(q, x, p);
}

#endif
