/* TinyC# の Math の超越関数。tcs2c の生成 C、Lua の module (runtime/lua)、
   .NET の TinySystem.Math (同じアルゴリズムの C# 版) が同じ結果を bit 単位で返す。
   三角関数系と log10 は CORE-MATH (正しく丸める)、exp / log / log2 / pow は
   musl の Arm 由来の実装を元にしている。

   結果は IEEE-754 の binary32 / binary64 と round-to-nearest に依存し、
   コンパイラの最適化で変わってはならない: FLT_EVAL_METHOD == 0 で、
   fast-math を使わず、積和の融合 (-ffp-contract=off) を止めて build する。 */
#ifndef TCS_MATH_H
#define TCS_MATH_H

/* tcs2c は runtime/math を生成 C に埋め込み、TCS_MATH_STATIC で static にする
   (host が同じ名前の関数を持っていても衝突しない) */
#ifdef TCS_MATH_STATIC
#define TCS_MATH_API static
#else
#define TCS_MATH_API
#endif

TCS_MATH_API float tcs_sinf(float x);
TCS_MATH_API float tcs_cosf(float x);
TCS_MATH_API float tcs_tanf(float x);
TCS_MATH_API float tcs_atan2f(float y, float x);
TCS_MATH_API float tcs_expf(float x);
TCS_MATH_API float tcs_logf(float x);
TCS_MATH_API float tcs_log2f(float x);
TCS_MATH_API float tcs_log10f(float x);
TCS_MATH_API float tcs_powf(float x, float y);

#endif
