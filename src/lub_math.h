/* どの OS でも同じ結果を返す数学関数 (musl の float 版)。
   ゲームの論理の計算が使う超越関数はこれを通す。
   Lua の math.* と ^ (src/lua_api.c、src/lua_pow.h) と tcs2c の生成 C が呼ぶ。
   sqrtf や floorf などは IEEE-754 で結果が決まるので OS のものを使う。 */
#ifndef LUB_MATH_H
#define LUB_MATH_H

float lub_sinf(float x);
float lub_cosf(float x);
float lub_tanf(float x);
float lub_asinf(float x);
float lub_acosf(float x);
float lub_atanf(float x);
float lub_atan2f(float y, float x);
float lub_expf(float x);
float lub_logf(float x);
float lub_log2f(float x);
float lub_log10f(float x);
float lub_powf(float x, float y);

/* tcs2c の生成 C は超越関数を TCS_MATH(name) で呼ぶ。
   生成 C の前でこの header を include すると lub_math を使う。 */
#define TCS_MATH(name) lub_##name

#endif
