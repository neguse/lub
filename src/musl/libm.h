/* musl の src/internal/libm.h の代わり。
   third_party/musl の float 版の関数が使う定義だけを MSVC でも通る形で持つ。
   libm.h を include しないソースもあるので、全ての TU に強制 include する。 */
#ifndef LUB_MUSL_LIBM_H
#define LUB_MUSL_LIBM_H

#include <float.h>
#include <math.h>
#include <stdint.h>
#include <string.h>

#include <features.h>

/* x87 の拡張精度で計算すると OS ごとに結果が変わる */
#if FLT_EVAL_METHOD != 0
#error "third_party/musl requires FLT_EVAL_METHOD == 0"
#endif

/* OS の libm と衝突しないよう、lub_ 始まりの名前で build する。
   OS の math.h の宣言を変えないよう、math.h を読んだ後に付け替える。 */
#define sinf lub_sinf
#define cosf lub_cosf
#define tanf lub_tanf
#define asinf lub_asinf
#define acosf lub_acosf
#define atanf lub_atanf
#define atan2f lub_atan2f
#define expf lub_expf
#define logf lub_logf
#define log2f lub_log2f
#define log10f lub_log10f
#define powf lub_powf
#define __sindf lub___sindf
#define __cosdf lub___cosdf
#define __tandf lub___tandf
#define __rem_pio2f lub___rem_pio2f
#define __rem_pio2_large lub___rem_pio2_large
#define __math_xflowf lub___math_xflowf
#define __math_uflowf lub___math_uflowf
#define __math_oflowf lub___math_oflowf
#define __math_divzerof lub___math_divzerof
#define __math_invalidf lub___math_invalidf
#include "../lub_math.h"

#ifndef M_PI_2
#define M_PI_2 1.57079632679489661923
#endif

#define WANT_ROUNDING 1
#define issignalingf_inline(x) 0
#define TOINT_INTRINSICS 0

#ifdef __GNUC__
#define predict_true(x) __builtin_expect(!!(x), 1)
#define predict_false(x) __builtin_expect(x, 0)
#else
#define predict_true(x) (x)
#define predict_false(x) (x)
#endif

static inline float eval_as_float(float x) {
  float y = x;
  return y;
}

static inline double eval_as_double(double x) {
  double y = x;
  return y;
}

static inline float fp_barrierf(float x) {
  volatile float y = x;
  return y;
}

static inline void fp_force_evalf(float x) {
  volatile float y;
  y = x;
}

static inline void fp_force_eval(double x) {
  volatile double y;
  y = x;
}

#define FORCE_EVAL(x)                                                          \
  do {                                                                         \
    if (sizeof(x) == sizeof(float))                                            \
      fp_force_evalf(x);                                                       \
    else                                                                       \
      fp_force_eval(x);                                                        \
  } while (0)

static inline uint32_t asuint(float f) {
  uint32_t i;
  memcpy(&i, &f, sizeof i);
  return i;
}

static inline float asfloat(uint32_t i) {
  float f;
  memcpy(&f, &i, sizeof f);
  return f;
}

static inline uint64_t asuint64(double f) {
  uint64_t i;
  memcpy(&i, &f, sizeof i);
  return i;
}

static inline double asdouble(uint64_t i) {
  double f;
  memcpy(&f, &i, sizeof f);
  return f;
}

#define GET_FLOAT_WORD(w, d)                                                   \
  do {                                                                         \
    (w) = asuint(d);                                                           \
  } while (0)

#define SET_FLOAT_WORD(d, w)                                                   \
  do {                                                                         \
    (d) = asfloat(w);                                                          \
  } while (0)

hidden int __rem_pio2_large(double *, double *, int, int, int);
hidden int __rem_pio2f(float, double *);
hidden float __sindf(double);
hidden float __cosdf(double);
hidden float __tandf(double, int);

hidden float __math_xflowf(uint32_t, float);
hidden float __math_uflowf(uint32_t);
hidden float __math_oflowf(uint32_t);
hidden float __math_divzerof(uint32_t);
hidden float __math_invalidf(float);

#endif
