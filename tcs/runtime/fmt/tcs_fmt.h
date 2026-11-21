/* TinyC# の数値の文字列化と、文字列の数としての読み取り。
   .NET の float.ToString() と float.ToString(format)、int.ToString(format) (InvariantCulture) と同じ文字列を返す。
   int.Parse(string) と float.Parse(string) (InvariantCulture) と同じ文字列を受けて同じ値を返す。
   tcs2c の生成 C と tcs VM が使う。読み取り (parse.c) は runtime/utf8 を使う。 */
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
  ((spec) != 'G' && (spec) != 'g' && (precision) > 0                           \
       ? (size_t)(precision) + 128                                             \
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

/* IL の string.format の変換 1 つ (printf.c)。spec は '%' から変換の文字まで (flags [-+ #0]、幅、精度)。
   値は kind で選ぶ: TCS_PRINTF_I32 は i、TCS_PRINTF_F32 は f、TCS_PRINTF_STR は s と slen (%s だけ)。
   %d / %x などは整数の値 (float は整数の値のときだけ)、%f / %e / %g / %a は double に昇格、%c は byte。
   out は TCS_PRINTF_MAX(slen) byte。書いた byte 数を返す。値が変換に合わなければ -1 で *error に理由 */
enum { TCS_PRINTF_I32 = 0, TCS_PRINTF_F32 = 1, TCS_PRINTF_STR = 2 };
#define TCS_PRINTF_MAX(slen) ((size_t)(slen) + 512)
TCS_FMT_API long tcs_printf_one(const char *spec, size_t speclen, int kind,
                                int32_t i, float f, const char *s, size_t slen,
                                char *out, const char **error);

/* text の length byte を .NET の int.Parse(string) (NumberStyles.Integer) と同じ規則で読む。
   前後の空白 (空白と HT、LF、VT、FF、CR)、先頭の符号 1 つ、1 桁以上の 10 進の数字で、末尾の NUL は読み飛ばす。
   読めて int の範囲に入れば 1 を返して *out に値を書き、それ以外は 0 を返す */
TCS_FMT_API int tcs_parse_i32(const char *text, size_t length, int32_t *out);

/* text の length byte を .NET の float.Parse(string) (NumberStyles.Float | AllowThousands) と同じ規則で読む。
   int に加えて小数点 1 つ、指数 (e と符号と数字)、小数点より前で数字の後の桁区切り ','。
   数として読めなければ、前後の空白 (char.IsWhiteSpace) を除いた Infinity と NaN の綴り (大文字小文字を問わず、前に符号 1 つ)。
   値は最も近い float (ちょうど中間は仮数が偶数の方、範囲を超えれば ±∞)。
   読めれば 1 を返して *out に値を書き、それ以外は 0 を返す */
TCS_FMT_API int tcs_parse_f32(const char *text, size_t length, float *out);

#endif
