/* Helpers shared by the runtime/math implementations. Written only in forms
   that avoid GCC / Clang extensions (__int128,
   __builtin_*) and give the same results on MSVC too.
   The C# version (TinySystem/TcsMath.Util.cs) mirrors the same forms. */
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

/* Returns a value with fixed bits as the result (0.0f / 0.0f and the like give
 * NaNs whose sign differs by CPU) */
#define TCS_NANF tcs_asfloat(0x7fc00000u)
#define TCS_INFF tcs_asfloat(0x7f800000u)

/* Rounds x with |x| < 2^51 to an integer, rounding to even (uses the rounding
 * of a round-to-nearest addition) */
static inline double tcs_roundeven_small(double x) {
  const double shift = 0x1.8p52;
  return (x + shift) - shift;
}

/* High and low halves of a 64-bit by 64-bit product (instead of an unsigned
 * __int128 product) */
static inline uint64_t tcs_mul64(uint64_t a, uint64_t b, uint64_t *hi) {
  uint64_t al = (uint32_t)a, ah = a >> 32, bl = (uint32_t)b, bh = b >> 32;
  uint64_t ll = al * bl, lh = al * bh, hl = ah * bl, hh = ah * bh;
  uint64_t mid = (ll >> 32) + (uint32_t)lh + (uint32_t)hl;
  *hi = hh + (lh >> 32) + (hl >> 32) + (mid >> 32);
  return (mid << 32) | (uint32_t)ll;
}

static inline int tcs_clz32(uint32_t x) {
  int n = 0;
  if (!(x & 0xffff0000u)) {
    n += 16;
    x <<= 16;
  }
  if (!(x & 0xff000000u)) {
    n += 8;
    x <<= 8;
  }
  if (!(x & 0xf0000000u)) {
    n += 4;
    x <<= 4;
  }
  if (!(x & 0xc0000000u)) {
    n += 2;
    x <<= 2;
  }
  if (!(x & 0x80000000u)) {
    n += 1;
  }
  return n;
}

/* Correctly rounds fmaf(a, b, c) using only double operations. a * b is exact
   in double, the error of the sum is computed with TwoSum, and the result is
   brought to double with round to odd before converting to float (double's
   precision is at least float's 24 + 2 bits, so the double rounding gives the
   same result as correct rounding). Overflow / underflow does not occur */
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

/* For p, a * b rounded to double, returns a * b - p exactly
   (instead of fma(a, b, -p). Veltkamp splitting and Dekker's product.
   It does not overflow / underflow in the value ranges where it is used) */
static inline double tcs_split_hi(double a) {
  double c = 134217729.0 * a; /* 2^27 + 1 */
  return c - (c - a);
}

static inline double tcs_mul_err(double a, double b, double p) {
  double ah = tcs_split_hi(a), al = a - ah;
  double bh = tcs_split_hi(b), bl = b - bh;
  return (((ah * bh - p) + ah * bl) + al * bh) + al * bl;
}

/* For q, y / x rounded to double, returns y - q * x exactly
   (instead of fma(-q, x, y). This remainder is exactly representable in double)
 */
static inline double tcs_div_rem(double y, double x, double q) {
  double p = q * x;
  return (y - p) - tcs_mul_err(q, x, p);
}

#endif
