/* IL の string.format の変換 1 つ (tcs_fmt.h)。tcs2c の生成 C と VM が同じものを使う。 */
#include "tcs_fmt.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

TCS_FMT_API long tcs_printf_one(const char *spec, size_t speclen, int kind, int32_t i, float f,
                                const char *s, size_t slen, char *out, const char **error) {
  char fmt[40];
  size_t n = speclen - 1; /* 変換の文字を除いた長さ */
  char conv;
  int written;
  if (speclen < 2 || speclen > 24 || spec[0] != '%') {
    *error = "invalid format string";
    return -1;
  }
  conv = spec[n];
  memcpy(fmt, spec, n);
  switch (conv) {
  case 'd':
  case 'i': {
    long long value;
    if (kind == TCS_PRINTF_I32)
      value = i;
    else if (kind == TCS_PRINTF_F32 && f == (float)(long long)f)
      value = (long long)f;
    else {
      *error = "number has no integer representation";
      return -1;
    }
    memcpy(fmt + n, "lld", 4);
    written = snprintf(out, TCS_PRINTF_MAX(0), fmt, value);
    break;
  }
  case 'x':
  case 'X':
  case 'o':
  case 'u': {
    unsigned long long value;
    if (kind == TCS_PRINTF_I32)
      value = (uint32_t)i;
    else if (kind == TCS_PRINTF_F32 && f == (float)(long long)f)
      value = (uint32_t)(int32_t)f;
    else {
      *error = "number has no integer representation";
      return -1;
    }
    fmt[n] = 'l';
    fmt[n + 1] = 'l';
    fmt[n + 2] = conv;
    fmt[n + 3] = '\0';
    written = snprintf(out, TCS_PRINTF_MAX(0), fmt, value);
    break;
  }
  case 'c':
    if (kind != TCS_PRINTF_I32) {
      *error = "number expected";
      return -1;
    }
    out[0] = (char)(unsigned char)i;
    return 1;
  case 'f':
  case 'F':
  case 'e':
  case 'E':
  case 'g':
  case 'G':
  case 'a':
  case 'A': {
    double value;
    if (kind == TCS_PRINTF_I32)
      value = i;
    else if (kind == TCS_PRINTF_F32)
      value = f;
    else {
      *error = "number expected";
      return -1;
    }
    fmt[n] = conv;
    fmt[n + 1] = '\0';
    written = snprintf(out, TCS_PRINTF_MAX(0), fmt, value);
    break;
  }
  case 's': {
    /* snprintf の %s は NUL で止まるので、NUL を含む string も slen の byte のまま自分で幅を埋める */
    size_t k = 1, width = 0, precision = slen, len, pad;
    int left = 0;
    if (kind != TCS_PRINTF_STR) {
      *error = "string expected";
      return -1;
    }
    while (k < n && (spec[k] == '-' || spec[k] == '+' || spec[k] == ' ' || spec[k] == '#' || spec[k] == '0')) {
      if (spec[k] == '-')
        left = 1;
      k++;
    }
    while (k < n && spec[k] >= '0' && spec[k] <= '9')
      width = width * 10 + (size_t)(spec[k++] - '0');
    if (k < n && spec[k] == '.') {
      k++;
      precision = 0;
      while (k < n && spec[k] >= '0' && spec[k] <= '9')
        precision = precision * 10 + (size_t)(spec[k++] - '0');
    }
    if (k != n) {
      *error = "invalid format string";
      return -1;
    }
    len = slen < precision ? slen : precision;
    pad = width > len ? width - len : 0;
    /* out は TCS_PRINTF_MAX(slen) byte */
    if (pad > TCS_PRINTF_MAX(0))
      pad = TCS_PRINTF_MAX(0);
    if (left) {
      if (len != 0)
        memcpy(out, s, len);
      memset(out + len, ' ', pad);
    } else {
      memset(out, ' ', pad);
      if (len != 0)
        memcpy(out + pad, s, len);
    }
    return (long)(len + pad);
  }
  default:
    *error = "invalid conversion to 'format'";
    return -1;
  }
  if (written < 0) {
    *error = "format";
    return -1;
  }
  return written;
}
