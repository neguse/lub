/* TinyC# の float の既定の文字列化。
   .NET の float.ToString() (InvariantCulture) と同じ文字列を返す。
   tcs2c の生成 C と Lua の module (runtime/lua) が使う。 */
#ifndef TCS_FMT_H
#define TCS_FMT_H

#include <stddef.h>

/* tcs2c は runtime/fmt を生成 C に埋め込み、TCS_FMT_STATIC で static にする */
#ifdef TCS_FMT_STATIC
#define TCS_FMT_API static
#else
#define TCS_FMT_API
#endif

/* 最長は "-0.000123456789" などの 15 文字 */
#define TCS_FORMAT_F32_MAX 16

/* out に書いた文字数を返す (終端の '\0' は書かない) */
TCS_FMT_API size_t tcs_format_f32(float value, char out[TCS_FORMAT_F32_MAX]);

#endif
