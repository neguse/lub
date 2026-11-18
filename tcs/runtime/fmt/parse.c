/* .NET の int.Parse(string) / float.Parse(string) (InvariantCulture) と同じ規則の数の読み取り。
   float は 10 進の数字の列のまま 2 の冪を掛けたり割ったりして [0.5, 1) に寄せ、仮数の 24 bit を取り出して丸める。
   掛け算と割り算は正確で、列 (TCS_DECIMAL_DIGITS 桁) に入らない桁は切り捨て、0 でない桁を捨てたという印を残す。
   丸めの境 (隣り合う float の中間) は 10 進で 113 桁までで、途中で掛けたり割ったりした値も列に収まる。
   切り捨てた値は境を下回らないので、列の値と印から境との大小が決まり、double を経由せずに正しく丸められる。 */
#include "../utf8/tcs_utf8.h"
#include "tcs_fmt.h"

#include <stdint.h>
#include <string.h>

/* 値が 0.d[0]d[1]...d[nd-1] × 10^dp の 10 進の数 (d は 0 から 9、先頭と末尾は 0 でない) */
#define TCS_DECIMAL_DIGITS 800
typedef struct {
  unsigned char d[TCS_DECIMAL_DIGITS];
  int nd;
  int dp;
  int trunc; /* d に入らなかった 0 でない桁がある (値は d より少し大きい) */
} TcsDecimal;

/* 1 度に掛けたり割ったりする 2 の冪の上限 (途中の値が 64 bit に入る) */
#define TCS_DECIMAL_MAX_SHIFT 60

static void tcs_decimal_trim(TcsDecimal *a) {
  while (a->nd > 0 && a->d[a->nd - 1] == 0)
    a->nd--;
  if (a->nd == 0)
    a->dp = 0;
}

/* a に 2^k を掛ける。下の桁から繰り上がりを運ぶ */
static void tcs_decimal_shift_left(TcsDecimal *a, unsigned k) {
  /* 掛けて増える桁は 2^60 の 19 桁まで */
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

/* a を 2^k で割る。上の桁から筆算で商を書く (書く位置は読む位置より前なので、同じ列に書ける) */
static void tcs_decimal_shift_right(TcsDecimal *a, unsigned k) {
  uint64_t rem = 0, mask = ((uint64_t)1 << k) - 1;
  int r = 0, w = 0;
  while (r < a->nd || rem != 0) {
    uint64_t cur = rem * 10 + (r < a->nd ? a->d[r] : 0);
    unsigned char q = (unsigned char)(cur >> k);
    rem = cur & mask;
    r++;
    if (w == 0 && q == 0)
      a->dp--; /* 商の先頭の 0 */
    else if (w < TCS_DECIMAL_DIGITS)
      a->d[w++] = q;
    else if (q != 0)
      a->trunc = 1;
  }
  a->nd = w;
  tcs_decimal_trim(a);
}

/* k > 0 は 2^k を掛け、k < 0 は 2^-k で割る */
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

/* 最も近い整数 (ちょうど中間は偶数、捨てた桁があれば中間より上)。整数部は 19 桁まで */
static uint64_t tcs_decimal_round(const TcsDecimal *a) {
  uint64_t n = 0;
  int i, f;
  for (i = 0; i < a->dp; i++)
    n = n * 10 + (i < a->nd ? a->d[i] : 0);
  if (a->dp < 0 || a->dp >= a->nd)
    return n; /* 小数部は 0.1 より小さいか、捨てた桁だけ */
  f = a->d[a->dp];
  if (f > 5 || (f == 5 && (a->nd > a->dp + 1 || a->trunc || (n & 1) != 0)))
    n++;
  return n;
}

/* [0.5, 1) に寄せるときの 2 の冪の指数: 3 |dp| (上限は TCS_DECIMAL_MAX_SHIFT) */
static int tcs_decimal_step(int dp) {
  int k = 3 * (dp < 0 ? -dp : dp);
  return k < TCS_DECIMAL_MAX_SHIFT ? k : TCS_DECIMAL_MAX_SHIFT;
}

/* 0 でない a を最も近い float (正の値) の bit にする */
static uint32_t tcs_decimal_to_f32_bits(TcsDecimal *a) {
  int exp = 0; /* 元の値 = a × 2^exp */
  uint64_t mant;
  /* 10^39 以上は ∞、10^-46 より小さい値は 0 (最小の非正規化数の半分 2^-150 より小さい) */
  if (a->dp > 39)
    return 0x7F800000u;
  if (a->dp < -45)
    return 0;
  /* [0.5, 1) に寄せる。
     1 以上は 8^dp で割る (10^(dp-1) 以上の値は 0.125 より小さくならない)。
     0.1 より小さければ 8^-dp を掛ける (10^dp より小さい値は 1 に届かない)。
     最後に 0.5 以上になるまで 2 倍する */
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
  /* 値は (2a) × 2^(exp-1) で 2a は [1, 2)。指数 exp-1 が -126 より小さければ非正規化数で、仮数の桁を減らす */
  exp--;
  if (exp < -126) {
    tcs_decimal_shift(a, exp + 126);
    exp = -126;
  }
  if (exp > 127)
    return 0x7F800000u;
  /* 仮数は 2a × 2^23 を丸めた整数 (正規化数なら 2^23 以上 2^24 以下) */
  tcs_decimal_shift(a, 24);
  mant = tcs_decimal_round(a);
  if (mant == ((uint64_t)1 << 24)) {
    mant >>= 1;
    exp++;
    if (exp > 127)
      return 0x7F800000u;
  }
  if ((mant & ((uint64_t)1 << 23)) == 0)
    return (uint32_t)mant; /* 非正規化数 (と 0) */
  return ((uint32_t)(exp + 127) << 23) | ((uint32_t)mant & 0x7FFFFFu);
}

/* .NET の IsWhite: 空白と HT、LF、VT、FF、CR */
static int tcs_parse_is_white(unsigned char c) {
  return c == ' ' || (c >= 0x09 && c <= 0x0D);
}

static int tcs_parse_is_digit(unsigned char c) { return c >= '0' && c <= '9'; }

/* 数の後ろの残り: 空白の後に NUL だけが続くなら読み終わり (.NET は末尾の NUL を読み飛ばす) */
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

/* NumberStyles.Float | AllowThousands の数 (.NET の TryParseNumber と同じ順に読む)。読めれば 1 で、*a に数字、*negative に符号 */
static int tcs_parse_decimal(const unsigned char *s, size_t length,
                             TcsDecimal *a, int *negative) {
  size_t i = 0;
  int digits = 0, point = 0;
  int64_t scale = 0; /* 小数点の位置 (a->dp になる) */
  a->nd = 0;
  a->trunc = 0;
  *negative = 0;
  /* 前の空白と符号 1 つ (符号の後に空白は置けない) */
  while (i < length && tcs_parse_is_white(s[i]))
    i++;
  if (i < length && (s[i] == '+' || s[i] == '-'))
    *negative = s[i++] == '-';
  /* 数字、小数点 1 つ、小数点より前で数字の後の桁区切り */
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
        scale--; /* 最初の 0 でない数字より前の小数部の 0 */
      }
    } else if (c == '.' && !point) {
      point = 1;
    } else if (c == ',' && digits && !point) {
      /* 桁区切りは位置を問わず読み飛ばす */
    } else {
      break;
    }
  }
  if (!digits)
    return 0;
  /* 指数。e の後に (符号と) 数字が無ければ e は数の外 */
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
      /* .NET は 9 桁を超える指数を int.MaxValue にし、数字の桁の位置を捨てる */
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
  /* 範囲の外は ∞ と 0 の判定 (tcs_decimal_to_f32_bits) が変わらない値に寄せる */
  a->dp = scale > 40 ? 40 : scale < -46 ? -46 : (int)scale;
  tcs_decimal_trim(a);
  return 1;
}

/* s[0, n) が小文字の word と、ASCII の大文字小文字を除いて同じか */
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

/* Infinity と NaN と、その前の符号 1 つ (-NaN も NaN)。
   .NET は数として読めない文字列の前後の空白 (char.IsWhiteSpace) を除いてから、大文字小文字を問わずに比べる */
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
