#include "tcs_utf8.h"

#include <string.h>

TCS_UTF8_API size_t tcs_utf8_decode(const unsigned char *s, size_t length,
                                    uint32_t *rune) {
  unsigned char c = s[0], low = 0x80, high = 0xBF;
  size_t need, i;
  uint32_t value;
  if (c < 0x80) {
    *rune = c;
    return 1;
  }
  if (c >= 0xC2 && c <= 0xDF) {
    need = 1;
    value = c & 0x1Fu;
  } else if (c >= 0xE0 && c <= 0xEF) {
    need = 2;
    value = c & 0x0Fu;
    if (c == 0xE0) low = 0xA0; /* overlong */
    if (c == 0xED) high = 0x9F; /* surrogate */
  } else if (c >= 0xF0 && c <= 0xF4) {
    need = 3;
    value = c & 0x07u;
    if (c == 0xF0) low = 0x90; /* overlong */
    if (c == 0xF4) high = 0x8F; /* U+10FFFF の先 */
  } else {
    *rune = 0xFFFD;
    return 1;
  }
  for (i = 1; i <= need; i++) {
    if (i >= length || s[i] < low || s[i] > high) {
      *rune = 0xFFFD;
      return i;
    }
    value = (value << 6) | (s[i] & 0x3Fu);
    low = 0x80;
    high = 0xBF;
  }
  *rune = value;
  return need + 1;
}

TCS_UTF8_API int32_t tcs_utf8_rune_count(const unsigned char *s, size_t length) {
  size_t at = 0;
  int32_t count = 0;
  uint32_t rune;
  while (at < length) {
    at += tcs_utf8_decode(s + at, length - at, &rune);
    count++;
  }
  return count;
}

TCS_UTF8_API int tcs_utf8_rune_range(const unsigned char *s, size_t length,
                                     int32_t start, int32_t count, size_t *from,
                                     size_t *to) {
  size_t at = 0;
  uint32_t rune;
  int32_t i;
  if (start < 0 || count < 0) return 0;
  for (i = 0; i < start; i++) {
    if (at >= length) return 0;
    at += tcs_utf8_decode(s + at, length - at, &rune);
  }
  *from = at;
  for (i = 0; i < count; i++) {
    if (at >= length) return 0;
    at += tcs_utf8_decode(s + at, length - at, &rune);
  }
  *to = at;
  return 1;
}

/* 読んだ 1 文字が壊れているか (正しい U+FFFD は 3 byte) */
static int tcs_utf8_is_broken(uint32_t rune, const unsigned char *s, size_t used) {
  return rune == 0xFFFD && !(used == 3 && s[0] == 0xEF);
}

TCS_UTF8_API size_t tcs_utf8_scrub_length(const unsigned char *s, size_t length) {
  size_t at = 0, total = 0;
  uint32_t rune;
  while (at < length) {
    size_t used = tcs_utf8_decode(s + at, length - at, &rune);
    total += tcs_utf8_is_broken(rune, s + at, used) ? 3 : used;
    at += used;
  }
  return total;
}

TCS_UTF8_API void tcs_utf8_scrub(const unsigned char *s, size_t length,
                                 unsigned char *out) {
  size_t at = 0;
  uint32_t rune;
  while (at < length) {
    size_t used = tcs_utf8_decode(s + at, length - at, &rune);
    if (tcs_utf8_is_broken(rune, s + at, used)) {
      memcpy(out, "\xEF\xBF\xBD", 3);
      out += 3;
    } else {
      memcpy(out, s + at, used);
      out += used;
    }
    at += used;
  }
}
