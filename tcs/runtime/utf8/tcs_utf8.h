/* TinyC# の文字列 (UTF-8) の操作。tcs2c の生成 C と Lua の module (runtime/lua) が同じ実装を使い、
   .NET の TinySystem.Text (TinySystem/Utf8.cs) が同じ結果を返す。
   壊れた byte 列は .NET の Encoding.UTF8 と同じ位置で U+FFFD 1 文字に読む。 */
#ifndef TCS_UTF8_H
#define TCS_UTF8_H

#include <stddef.h>
#include <stdint.h>

/* tcs2c は runtime/utf8 を生成 C に埋め込み、TCS_UTF8_STATIC で static にする */
#ifdef TCS_UTF8_STATIC
#define TCS_UTF8_API static
#else
#define TCS_UTF8_API
#endif

/* 先頭の 1 文字を rune に読み、消費した byte 数を返す。length は 1 以上 */
TCS_UTF8_API size_t tcs_utf8_decode(const unsigned char *s, size_t length,
                                    uint32_t *rune);

TCS_UTF8_API int32_t tcs_utf8_rune_count(const unsigned char *s, size_t length);

/* start 文字目から count 文字の byte 範囲 [*from, *to)。文字列の範囲を出るなら 0 を返す */
TCS_UTF8_API int tcs_utf8_rune_range(const unsigned char *s, size_t length,
                                     int32_t start, int32_t count, size_t *from,
                                     size_t *to);

/* 壊れた並びを U+FFFD にした byte 列の長さと、その書き出し */
TCS_UTF8_API size_t tcs_utf8_scrub_length(const unsigned char *s, size_t length);
TCS_UTF8_API void tcs_utf8_scrub(const unsigned char *s, size_t length,
                                 unsigned char *out);

#endif
