/* TinyC# の float の文字列化。
   .NET の float.ToString() と float.ToString(format) (InvariantCulture) と同じ文字列を返す。
   tcs2c の生成 C と Lua の module (runtime/lua) が使う。 */
#ifndef TCS_FMT_H
#define TCS_FMT_H

#include <stddef.h>
#include <stdint.h>

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

/* 書式 spec ('G' / 'g' / 'E' / 'e') の最長。
   E は小数部が precision 桁、G は正確な値の桁 (112 桁まで) で終わる */
#define TCS_FORMAT_F32_SPEC_MAX(spec, precision)                               \
  (((spec) == 'E' || (spec) == 'e') && (precision) > 0                        \
       ? (size_t)(precision) + 128                                            \
       : (size_t)128)

/* 書式 spec と精度 precision (0 から 999999999、書式に精度が無ければ -1)。
   out は TCS_FORMAT_F32_SPEC_MAX(spec, precision) 文字。
   書いた文字数を返す (終端の '\0' は書かない) */
TCS_FMT_API size_t tcs_format_f32_spec(float value, char spec,
                                       int32_t precision, char *out);

#endif
