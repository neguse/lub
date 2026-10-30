/* TinyC# の数値の文字列化。
   .NET の float.ToString() と float.ToString(format)、int.ToString(format) (InvariantCulture) と同じ文字列を返す。
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

/* 書式 spec ('G' / 'E' / 'F' / 'N' と小文字) の最長。
   E / F / N は小数部が precision 桁、G は正確な値の桁 (112 桁まで) で終わる */
#define TCS_FORMAT_F32_SPEC_MAX(spec, precision)                               \
  ((spec) != 'G' && (spec) != 'g' && (precision) > 0                          \
       ? (size_t)(precision) + 128                                            \
       : (size_t)128)

/* 書式 spec と精度 precision (0 から 999999999、書式に精度が無ければ -1)。
   out は TCS_FORMAT_F32_SPEC_MAX(spec, precision) 文字。
   書いた文字数を返す (終端の '\0' は書かない) */
TCS_FMT_API size_t tcs_format_f32_spec(float value, char spec,
                                       int32_t precision, char *out);

/* 書式 spec ('G' / 'E' / 'F' / 'N' / 'D' / 'X' と小文字) の最長 */
#define TCS_FORMAT_I32_SPEC_MAX(precision)                                     \
  ((precision) > 0 ? (size_t)(precision) + 32 : (size_t)32)

/* 引数と返す値は tcs_format_f32_spec と同じ。out は TCS_FORMAT_I32_SPEC_MAX(precision) 文字 */
TCS_FMT_API size_t tcs_format_i32_spec(int32_t value, char spec,
                                       int32_t precision, char *out);

#endif
