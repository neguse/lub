/* Number parsing with the same rules as .NET's int.Parse(string) /
   float.Parse(string) (InvariantCulture). A float stays a sequence of decimal
   digits while it is multiplied or divided by powers of 2 to bring it into
   [0.5, 1), and then the 24 bits of the mantissa are taken and rounded.
   Multiplication and division are exact; digits that do not fit in the sequence
   (TCS_DECIMAL_DIGITS digits) are truncated, and a mark records that a nonzero
   digit was dropped. A rounding boundary (the midpoint of adjacent floats) has
   at most 113 decimal digits, and the intermediate values of the
   multiplications and divisions also fit in the sequence. A truncated value
   never falls below a boundary, so the value in the sequence and the mark
   decide the order against the boundary, and rounding is correct without going
   through double. */
#include "../utf8/tcs_utf8.h"
#include "tcs_fmt.h"

#include <stdint.h>
#include <string.h>

/* A decimal number whose value is 0.d[0]d[1]...d[nd-1] × 10^dp (d is 0 to 9;
 * the first and last are not 0) */
#define TCS_DECIMAL_DIGITS 800
typedef struct {
  unsigned char d[TCS_DECIMAL_DIGITS];
  int nd;
  int dp;
  int trunc; /* There is a nonzero digit that did not fit in d (the value is
                slightly larger than d) */
} TcsDecimal;

/* The upper limit of the power of 2 multiplied or divided at once (intermediate
 * values fit in 64 bits) */
#define TCS_DECIMAL_MAX_SHIFT 60

static void tcs_decimal_trim(TcsDecimal *a) {
  while (a->nd > 0 && a->d[a->nd - 1] == 0)
    a->nd--;
  if (a->nd == 0)
    a->dp = 0;
}

/* Multiplies a by 2^k. The carry is propagated from the lowest digit */
static void tcs_decimal_shift_left(TcsDecimal *a, unsigned k) {
  /* Multiplying adds at most the 19 digits of 2^60 */
  unsigned char out[TCS_DECIMAL_DIGITS + 20];
  int w = (int)sizeof out, r, count, i;
  uint64_t n = 0;
  for (r = a->nd - 1; r >= 0; r--) {
    n += (uint64_t)a->d[r] << k;
    out[--w] = (unsigned char)(n % 10);
    n /= 10;
  }
  while (n > 0) {
    out[--w] = (unsigned char)(n % 10);
    n /= 10;
  }
  count = (int)sizeof out - w;
  a->dp += count - a->nd;
  for (i = TCS_DECIMAL_DIGITS; i < count; i++)
    if (out[w + i] != 0)
      a->trunc = 1;
  if (count > TCS_DECIMAL_DIGITS)
    count = TCS_DECIMAL_DIGITS;
  memcpy(a->d, out + w, (size_t)count);
  a->nd = count;
  tcs_decimal_trim(a);
}

/* Divides a by 2^k. The quotient is written by long division from the highest
 * digit (the write position is before the read position, so it can be written
 * in the same sequence) */
static void tcs_decimal_shift_right(TcsDecimal *a, unsigned k) {
  uint64_t rem = 0, mask = ((uint64_t)1 << k) - 1;
  int r = 0, w = 0;
  while (r < a->nd || rem != 0) {
    uint64_t cur = rem * 10 + (r < a->nd ? a->d[r] : 0);
    unsigned char q = (unsigned char)(cur >> k);
    rem = cur & mask;
    r++;
    if (w == 0 && q == 0)
      a->dp--; /* A leading 0 of the quotient */
    else if (w < TCS_DECIMAL_DIGITS)
      a->d[w++] = q;
    else if (q != 0)
      a->trunc = 1;
  }
  a->nd = w;
  tcs_decimal_trim(a);
}

/* k > 0 multiplies by 2^k, and k < 0 divides by 2^-k */
static void tcs_decimal_shift(TcsDecimal *a, int k) {
  while (a->nd > 0 && k > 0) {
    int step = k < TCS_DECIMAL_MAX_SHIFT ? k : TCS_DECIMAL_MAX_SHIFT;
    tcs_decimal_shift_left(a, (unsigned)step);
    k -= step;
  }
  while (a->nd > 0 && k < 0) {
    int step = -k < TCS_DECIMAL_MAX_SHIFT ? -k : TCS_DECIMAL_MAX_SHIFT;
    tcs_decimal_shift_right(a, (unsigned)step);
    k += step;
  }
}

/* The nearest integer (an exact midpoint goes to even; above the midpoint when
 * digits were dropped). The integer part has at most 19 digits */
static uint64_t tcs_decimal_round(const TcsDecimal *a) {
  uint64_t n = 0;
  int i, f;
  for (i = 0; i < a->dp; i++)
    n = n * 10 + (i < a->nd ? a->d[i] : 0);
  if (a->dp < 0 || a->dp >= a->nd)
    return n; /* The fraction is less than 0.1, or only dropped digits */
  f = a->d[a->dp];
  if (f > 5 || (f == 5 && (a->nd > a->dp + 1 || a->trunc || (n & 1) != 0)))
    n++;
  return n;
}

/* The exponent of the power of 2 used to bring the value into [0.5, 1): 3 |dp|
 * (at most TCS_DECIMAL_MAX_SHIFT) */
static int tcs_decimal_step(int dp) {
  int k = 3 * (dp < 0 ? -dp : dp);
  return k < TCS_DECIMAL_MAX_SHIFT ? k : TCS_DECIMAL_MAX_SHIFT;
}

/* Turns a nonzero a into the bits of the nearest float (a positive value) */
static uint32_t tcs_decimal_to_f32_bits(TcsDecimal *a) {
  int exp = 0; /* original value = a × 2^exp */
  uint64_t mant;
  /* 10^39 or more is ∞, and a value less than 10^-46 is 0 (less than 2^-150,
   * half of the smallest subnormal) */
  if (a->dp > 39)
    return 0x7F800000u;
  if (a->dp < -45)
    return 0;
  /* Brings the value into [0.5, 1).
     A value of 1 or more is divided by 8^dp (a value of 10^(dp-1) or more does
     not go below 0.125). A value less than 0.1 is multiplied by 8^-dp (a value
     less than 10^dp does not reach 1). Finally it is doubled until it is 0.5 or
     more */
  while (a->dp > 0) {
    int k = tcs_decimal_step(a->dp);
    tcs_decimal_shift(a, -k);
    exp += k;
  }
  while (a->dp < 0) {
    int k = tcs_decimal_step(a->dp);
    tcs_decimal_shift(a, k);
    exp -= k;
  }
  while (a->d[0] < 5) {
    tcs_decimal_shift(a, 1);
    exp--;
  }
  /* The value is (2a) × 2^(exp-1) with 2a in [1, 2). When the exponent exp-1 is
   * less than -126 it is a subnormal, and the mantissa digits are reduced */
  exp--;
  if (exp < -126) {
    tcs_decimal_shift(a, exp + 126);
    exp = -126;
  }
  if (exp > 127)
    return 0x7F800000u;
  /* The mantissa is the integer 2a × 2^23 rounded (from 2^23 to 2^24 for a
   * normal number) */
  tcs_decimal_shift(a, 24);
  mant = tcs_decimal_round(a);
  if (mant == ((uint64_t)1 << 24)) {
    mant >>= 1;
    exp++;
    if (exp > 127)
      return 0x7F800000u;
  }
  if ((mant & ((uint64_t)1 << 23)) == 0)
    return (uint32_t)mant; /* A subnormal (and 0) */
  return ((uint32_t)(exp + 127) << 23) | ((uint32_t)mant & 0x7FFFFFu);
}

/* .NET's IsWhite: space and HT, LF, VT, FF, CR */
static int tcs_parse_is_white(unsigned char c) {
  return c == ' ' || (c >= 0x09 && c <= 0x0D);
}

static int tcs_parse_is_digit(unsigned char c) { return c >= '0' && c <= '9'; }

/* The rest after the number: parsing ends when only NULs follow the whitespace
 * (.NET skips trailing NULs) */
static int tcs_parse_rest_is_empty(const unsigned char *s, size_t i,
                                   size_t length) {
  while (i < length && tcs_parse_is_white(s[i]))
    i++;
  while (i < length && s[i] == 0)
    i++;
  return i == length;
}

TCS_FMT_API int tcs_parse_i32(const char *text, size_t length, int32_t *out) {
  const unsigned char *s = (const unsigned char *)text;
  size_t i = 0, start;
  int negative = 0;
  int64_t value = 0;
  while (i < length && tcs_parse_is_white(s[i]))
    i++;
  if (i < length && (s[i] == '+' || s[i] == '-'))
    negative = s[i++] == '-';
  start = i;
  for (; i < length && tcs_parse_is_digit(s[i]); i++)
    if (value <= (int64_t)INT32_MAX + 1)
      value = value * 10 + (s[i] - '0');
  if (i == start || !tcs_parse_rest_is_empty(s, i, length))
    return 0;
  if (negative)
    value = -value;
  if (value < INT32_MIN || value > INT32_MAX)
    return 0;
  *out = (int32_t)value;
  return 1;
}

/* A number in NumberStyles.Float | AllowThousands (read in the same order as
 * .NET's TryParseNumber). 1 when it can be read, with the digits in *a and the
 * sign in *negative */
static int tcs_parse_decimal(const unsigned char *s, size_t length,
                             TcsDecimal *a, int *negative) {
  size_t i = 0;
  int digits = 0, point = 0;
  int64_t scale = 0; /* The position of the decimal point (becomes a->dp) */
  a->nd = 0;
  a->trunc = 0;
  *negative = 0;
  /* Leading whitespace and one sign (whitespace cannot follow the sign) */
  while (i < length && tcs_parse_is_white(s[i]))
    i++;
  if (i < length && (s[i] == '+' || s[i] == '-'))
    *negative = s[i++] == '-';
  /* Digits, one decimal point, and a group separator before the decimal point
   * and after a digit */
  for (; i < length; i++) {
    unsigned char c = s[i];
    if (tcs_parse_is_digit(c)) {
      digits = 1;
      if (c != '0' || a->nd > 0) {
        if (a->nd < TCS_DECIMAL_DIGITS)
          a->d[a->nd++] = (unsigned char)(c - '0');
        else if (c != '0')
          a->trunc = 1;
        if (!point)
          scale++;
      } else if (point) {
        scale--; /* A 0 of the fraction before the first nonzero digit */
      }
    } else if (c == '.' && !point) {
      point = 1;
    } else if (c == ',' && digits && !point) {
      /* A group separator is skipped wherever it is */
    } else {
      break;
    }
  }
  if (!digits)
    return 0;
  /* The exponent. When no digit follows e (and a sign), e is outside the number
   */
  if (i < length && (s[i] == 'e' || s[i] == 'E')) {
    size_t j = i + 1;
    int exp_negative = 0;
    if (j < length && (s[j] == '+' || s[j] == '-'))
      exp_negative = s[j++] == '-';
    if (j < length && tcs_parse_is_digit(s[j])) {
      int64_t e = 0;
      int huge = 0;
      for (; j < length && tcs_parse_is_digit(s[j]); j++) {
        if (e >= 100000000)
          huge = 1;
        else
          e = e * 10 + (s[j] - '0');
      }
      /* .NET turns an exponent of more than 9 digits into int.MaxValue and
       * discards the position of the digits */
      if (huge) {
        e = INT32_MAX;
        scale = 0;
      }
      scale += exp_negative ? -e : e;
      i = j;
    }
  }
  if (!tcs_parse_rest_is_empty(s, i, length))
    return 0;
  /* Out of range is clamped to a value that does not change the ∞ and 0
   * decisions (tcs_decimal_to_f32_bits) */
  a->dp = scale > 40 ? 40 : scale < -46 ? -46 : (int)scale;
  tcs_decimal_trim(a);
  return 1;
}

/* Whether s[0, n) equals the lowercase word, ignoring ASCII case */
static int tcs_parse_equals_ignore_case(const unsigned char *s, size_t n,
                                        const char *word) {
  size_t i;
  if (n != strlen(word))
    return 0;
  for (i = 0; i < n; i++) {
    unsigned char c = s[i];
    if (c >= 'A' && c <= 'Z')
      c = (unsigned char)(c + 32);
    if (c != (unsigned char)word[i])
      return 0;
  }
  return 1;
}

/* Infinity and NaN, and one sign before them (-NaN is also NaN).
   .NET trims the whitespace (char.IsWhiteSpace) around a string that cannot be
   read as a number, and then compares ignoring case */
static int tcs_parse_symbol(const unsigned char *s, size_t length,
                            uint32_t *bits) {
  size_t from, to, n;
  int negative = 0;
  tcs_utf8_trim(s, length, &from, &to);
  s += from;
  n = to - from;
  if (n > 0 && (s[0] == '+' || s[0] == '-')) {
    negative = s[0] == '-';
    s++;
    n--;
  }
  if (tcs_parse_equals_ignore_case(s, n, "infinity")) {
    *bits = negative ? 0xFF800000u : 0x7F800000u;
    return 1;
  }
  if (tcs_parse_equals_ignore_case(s, n, "nan")) {
    *bits = 0xFFC00000u; /* float.NaN */
    return 1;
  }
  return 0;
}

TCS_FMT_API int tcs_parse_f32(const char *text, size_t length, float *out) {
  const unsigned char *s = (const unsigned char *)text;
  TcsDecimal a;
  int negative;
  uint32_t bits;
  if (tcs_parse_decimal(s, length, &a, &negative)) {
    bits = a.nd == 0 ? 0 : tcs_decimal_to_f32_bits(&a);
    if (negative)
      bits |= 0x80000000u;
  } else if (!tcs_parse_symbol(s, length, &bits)) {
    return 0;
  }
  memcpy(out, &bits, sizeof bits);
  return 1;
}
