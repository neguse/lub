/* tcs VM の runtime の関数 (CALLR)。TinySystem の面 (il-spec §13) の C 実装で、意味は tcs2c の runtime と
   runtime/tinysystem.lua と同じ。文字列化・数学関数・UTF-8 は runtime/fmt・runtime/math・runtime/utf8 の
   同じ C を呼ぶ。引数と戻り値の種類の文字は tcsvm_internal.h の TvmIntrinsic を見よ。 */
#include "tcsvm_internal.h"

#include "../../runtime/fmt/tcs_fmt.h"
#include "../../runtime/math/tcs_math.h"
#include "../../runtime/sort/tcs_sort.h"
#include "../../runtime/utf8/tcs_utf8.h"

#include <math.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define S(slot) ((TvmString *)(slot).p)
#define SDATA(s) ((const unsigned char *)STR_DATA(s))

static TvmStatus oom(TvmVm *vm) { return tvm_fault_msg(vm, "out of memory"); }

static TvmStatus ret_str(TvmVm *vm, TvmSlot *rets, const char *data, size_t len) {
  TvmString *s = tvm_str_new(vm, data, len);
  if (!s)
    return oom(vm);
  rets[0].p = s;
  return TVM_OK;
}

#define NONNULL_ARG(p, what)                                                   \
  if (!(p))                                                                    \
  return tvm_fault_msg(vm, "null reference" what)

/* ---- 文字列化 ---- */

/* 書式 "G"、"E2" など (文字 + 9 桁までの精度) の精度。無ければ -1、形が違えば -2 */
static int32_t format_precision(const TvmString *spec, const char *letters) {
  if (!spec || spec->len < 1 || spec->len > 10 || !strchr(letters, STR_DATA(spec)[0]))
    return -2;
  int32_t precision = -1;
  for (int32_t i = 1; i < spec->len; i++) {
    char c = STR_DATA(spec)[i];
    if (c < '0' || c > '9')
      return -2;
    precision = (precision < 0 ? 0 : precision * 10) + (c - '0');
  }
  return precision;
}

static TvmStatus fmt_float(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  int32_t p = format_precision(S(a[1]), "GgEeFfNnRr");
  if (p == -2)
    return tvm_fault_msg(vm, "unsupported number format");
  char spec = STR_DATA(S(a[1]))[0];
  if (spec == 'R' || spec == 'r') {
    spec = spec == 'R' ? 'G' : 'g';
    p = -1;
  }
  char *out = (char *)malloc(TCS_FORMAT_F32_SPEC_MAX(spec, p));
  if (!out)
    return oom(vm);
  size_t n = tcs_format_f32_spec(a[0].f, spec, p, out);
  TvmStatus st = ret_str(vm, r, out, n);
  free(out);
  return st;
}

static TvmStatus fmt_int(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  int32_t p = format_precision(S(a[1]), "GgEeFfNnDdXxRr");
  if (p == -2)
    return tvm_fault_msg(vm, "unsupported number format");
  char spec = STR_DATA(S(a[1]))[0];
  if (spec == 'R' || spec == 'r') {
    spec = 'G';
    p = -1;
  }
  char *out = (char *)malloc(TCS_FORMAT_I32_SPEC_MAX(p));
  if (!out)
    return oom(vm);
  size_t n = tcs_format_i32_spec(a[0].i, spec, p, out);
  TvmStatus st = ret_str(vm, r, out, n);
  free(out);
  return st;
}

/* enum の文字列化 (il-spec §13 の String.FormatEnum)。spec は `名前:値` を `,` で並べた列で、
   Flags は先頭が `*` (値の符号なし昇順に並ぶ) */
static TvmStatus fmt_enum(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *spec = S(a[1]);
  int32_t v = a[0].i;
  const char *p = STR_DATA(spec), *end = p + spec->len;
  int flags = p < end && *p == '*';
  if (flags)
    p++;
  /* member を読む */
  const char *names[256];
  size_t lens[256];
  int32_t values[256];
  int n = 0;
  while (p < end && n < 256) {
    const char *colon = memchr(p, ':', (size_t)(end - p));
    if (!colon)
      break;
    const char *comma = memchr(colon, ',', (size_t)(end - colon));
    if (!comma)
      comma = end;
    names[n] = p;
    lens[n] = (size_t)(colon - p);
    char num[16] = {0};
    size_t nl = (size_t)(comma - colon - 1) < 15 ? (size_t)(comma - colon - 1) : 15;
    memcpy(num, colon + 1, nl);
    values[n] = (int32_t)strtoll(num, NULL, 10);
    n++;
    p = comma + 1;
  }
  char numbuf[16];
  if (!flags) {
    for (int i = 0; i < n; i++)
      if (values[i] == v)
        return ret_str(vm, r, names[i], lens[i]);
  } else {
    if (v == 0) {
      if (n > 0 && values[0] == 0)
        return ret_str(vm, r, names[0], lens[0]);
    } else {
      uint32_t rest = (uint32_t)v;
      int found[256], nf = 0;
      for (int i = n - 1; i >= 0; i--) {
        uint32_t cur = (uint32_t)values[i];
        if (cur != 0 && (rest & cur) == cur) {
          rest &= ~cur;
          found[nf++] = i;
          if (rest == 0)
            break;
        }
      }
      if (rest == 0) {
        size_t total = 0;
        for (int k = 0; k < nf; k++)
          total += lens[found[k]] + (k ? 2 : 0);
        char *out = (char *)malloc(total + 1);
        if (!out)
          return oom(vm);
        size_t at = 0;
        for (int k = nf - 1; k >= 0; k--) {
          memcpy(out + at, names[found[k]], lens[found[k]]);
          at += lens[found[k]];
          if (k) {
            memcpy(out + at, ", ", 2);
            at += 2;
          }
        }
        TvmStatus st = ret_str(vm, r, out, at);
        free(out);
        return st;
      }
    }
  }
  int nn = snprintf(numbuf, sizeof numbuf, "%d", v);
  return ret_str(vm, r, numbuf, (size_t)nn);
}

/* ---- string ---- */

static TvmStatus str_trim(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  size_t from, to;
  tcs_utf8_trim(SDATA(s), (size_t)s->len, &from, &to);
  return ret_str(vm, r, STR_DATA(s) + from, to - from);
}

static const char *find_bytes(const char *h, size_t hl, const char *n, size_t nl) {
  if (nl == 0)
    return h;
  for (size_t i = 0; i + nl <= hl; i++)
    if (memcmp(h + i, n, nl) == 0)
      return h + i;
  return NULL;
}

static TvmStatus str_replace(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]), *old = S(a[1]), *nw = S(a[2]);
  NONNULL_ARG(s && old, "");
  if (old->len == 0)
    return tvm_fault_msg(vm, "oldValue cannot be empty");
  size_t nl = nw ? (size_t)nw->len : 0;
  /* 長さを数えてから書く */
  size_t total = 0;
  const char *p = STR_DATA(s), *end = p + s->len;
  for (;;) {
    const char *hit = find_bytes(p, (size_t)(end - p), STR_DATA(old), (size_t)old->len);
    if (!hit) {
      total += (size_t)(end - p);
      break;
    }
    total += (size_t)(hit - p) + nl;
    p = hit + old->len;
  }
  char *out = (char *)malloc(total + 1);
  if (!out)
    return oom(vm);
  size_t at = 0;
  p = STR_DATA(s);
  for (;;) {
    const char *hit = find_bytes(p, (size_t)(end - p), STR_DATA(old), (size_t)old->len);
    if (!hit) {
      memcpy(out + at, p, (size_t)(end - p));
      at += (size_t)(end - p);
      break;
    }
    memcpy(out + at, p, (size_t)(hit - p));
    at += (size_t)(hit - p);
    if (nl)
      memcpy(out + at, STR_DATA(nw), nl);
    at += nl;
    p = hit + old->len;
  }
  TvmStatus st = ret_str(vm, r, out, at);
  free(out);
  return st;
}

static TvmStatus str_contains(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]), *sub = S(a[1]);
  NONNULL_ARG(s && sub, "");
  r[0].i = find_bytes(STR_DATA(s), (size_t)s->len, STR_DATA(sub), (size_t)sub->len) != NULL;
  return TVM_OK;
}

static TvmStatus str_starts(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]), *p = S(a[1]);
  NONNULL_ARG(s && p, "");
  r[0].i = p->len <= s->len && memcmp(STR_DATA(s), STR_DATA(p), (size_t)p->len) == 0;
  return TVM_OK;
}

static TvmStatus str_ends(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]), *p = S(a[1]);
  NONNULL_ARG(s && p, "");
  r[0].i = p->len <= s->len && memcmp(STR_DATA(s) + s->len - p->len, STR_DATA(p), (size_t)p->len) == 0;
  return TVM_OK;
}

static TvmStatus str_empty(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = !a[0].p || S(a[0])->len == 0;
  return TVM_OK;
}

/* .NET の ordinal (UTF-16 の code unit の順)。UTF-8 の byte の順と違うのは U+10000 以上と U+E000-U+FFFF の間だけ
   (runtime/lua/tcs_rt_lua.c の tcs_rt_compare_utf16 と同じ) */
static int compare_utf16(const unsigned char *a, size_t la, const unsigned char *b, size_t lb) {
  size_t n = la < lb ? la : lb;
  for (size_t i = 0; i < n; i++) {
    if (a[i] == b[i])
      continue;
    if (a[i] >= 0xF0 && a[i] <= 0xF4 && b[i] >= 0xEE && b[i] <= 0xEF)
      return -1;
    if (b[i] >= 0xF0 && b[i] <= 0xF4 && a[i] >= 0xEE && a[i] <= 0xEF)
      return 1;
    return a[i] < b[i] ? -1 : 1;
  }
  return la < lb ? -1 : la > lb ? 1 : 0;
}

int tvm_str_compare(const TvmString *a, const TvmString *b) {
  if (!a)
    return b ? -1 : 0;
  if (!b)
    return 1;
  return compare_utf16(SDATA(a), (size_t)a->len, SDATA(b), (size_t)b->len);
}

static TvmStatus str_compare(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = tvm_str_compare(S(a[0]), S(a[1]));
  return TVM_OK;
}

/* Join の要素の文字列化 (連結と同じ: float は runtime/fmt、bool は True / False、null は空) */
static size_t elem_str(const TvmType *lt, const char *e, char *buf, const char **out) {
  switch (lt->elem_kind) {
  case TVM_K_I32:
    *out = buf;
    return (size_t)snprintf(buf, 32, "%d", *(const int32_t *)e);
  case TVM_K_F32:
    *out = buf;
    return tcs_format_f32(*(const float *)e, buf);
  case TVM_K_BOOL:
    *out = *(const uint8_t *)e ? "True" : "False";
    return *(const uint8_t *)e ? 4 : 5;
  default: {
    const TvmString *s = *(const TvmString *const *)e;
    *out = s ? STR_DATA(s) : "";
    return s ? (size_t)s->len : 0;
  }
  }
}

static TvmStatus str_join(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *sep = S(a[0]);
  const TvmList *l = (const TvmList *)a[1].p;
  NONNULL_ARG(l, "");
  if (l->type->elem_kind == TVM_K_REF && l->type->elem_type != vm->string_type)
    return tvm_fault_msg(vm, "ToString of this value is not supported");
  size_t sl = sep ? (size_t)sep->len : 0, total = 0;
  char buf[32];
  const char *piece;
  for (int32_t i = 0; i < l->count; i++)
    total += elem_str(l->type, l->data + (size_t)i * l->type->elem_size, buf, &piece) + (i ? sl : 0);
  char *out = (char *)malloc(total + 1);
  if (!out)
    return oom(vm);
  size_t at = 0;
  for (int32_t i = 0; i < l->count; i++) {
    if (i && sl) {
      memcpy(out + at, STR_DATA(sep), sl);
      at += sl;
    }
    size_t n = elem_str(l->type, l->data + (size_t)i * l->type->elem_size, buf, &piece);
    memcpy(out + at, piece, n);
    at += n;
  }
  TvmStatus st = ret_str(vm, r, out, at);
  free(out);
  return st;
}

static bool list_push_str(TvmVm *vm, TvmList *l, const char *p, size_t n) {
  TvmString *s = tvm_str_new(vm, p, n);
  if (!s || (l->count == l->cap && !tvm_list_grow(vm, l)))
    return false;
  ((TvmString **)l->data)[l->count++] = s;
  return true;
}

/* Split(sep): a[0] は結果を足す空の List<string> (呼ぶ側が作る)。sep が空なら元の文字列 1 つ */
static TvmStatus str_split(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  TvmList *l = (TvmList *)a[0].p;
  const TvmString *s = S(a[1]), *sep = S(a[2]);
  NONNULL_ARG(s, "");
  const char *p = STR_DATA(s), *end = p + s->len;
  if (!sep || sep->len == 0)
    return list_push_str(vm, l, p, (size_t)s->len) ? TVM_OK : oom(vm);
  for (;;) {
    const char *hit = find_bytes(p, (size_t)(end - p), STR_DATA(sep), (size_t)sep->len);
    if (!hit)
      return list_push_str(vm, l, p, (size_t)(end - p)) ? TVM_OK : oom(vm);
    if (!list_push_str(vm, l, p, (size_t)(hit - p)))
      return oom(vm);
    p = hit + sep->len;
  }
}

/* 引数なしの Split: 空白 (char.IsWhiteSpace) で分ける */
static TvmStatus str_split_ws(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  TvmList *l = (TvmList *)a[0].p;
  const TvmString *s = S(a[1]);
  NONNULL_ARG(s, "");
  size_t pos = 0, start, stop;
  while (tcs_utf8_find_white_space(SDATA(s), (size_t)s->len, pos, &start, &stop)) {
    if (!list_push_str(vm, l, STR_DATA(s) + pos, start - pos))
      return oom(vm);
    pos = stop;
  }
  return list_push_str(vm, l, STR_DATA(s) + pos, (size_t)s->len - pos) ? TVM_OK : oom(vm);
}

static TvmStatus str_upper(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  TvmString *o = tvm_str_new(vm, STR_DATA(s), (size_t)s->len);
  if (!o)
    return oom(vm);
  for (int32_t i = 0; i < o->len; i++) {
    char c = STR_DATA(o)[i];
    if (c >= 'a' && c <= 'z')
      STR_DATA(o)[i] = (char)(c - 32);
  }
  r[0].p = o;
  return TVM_OK;
}

static TvmStatus str_lower(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  TvmString *o = tvm_str_new(vm, STR_DATA(s), (size_t)s->len);
  if (!o)
    return oom(vm);
  for (int32_t i = 0; i < o->len; i++) {
    char c = STR_DATA(o)[i];
    if (c >= 'A' && c <= 'Z')
      STR_DATA(o)[i] = (char)(c + 32);
  }
  r[0].p = o;
  return TVM_OK;
}

static TvmStatus str_bytelen(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  NONNULL_ARG(a[0].p, "");
  r[0].i = S(a[0])->len;
  return TVM_OK;
}

/* int.Parse / float.Parse / TryParse (Math.TryParseInt と同じ: 前後の空白、符号、10 進の数字) */
static int parse_int(const TvmString *s, int32_t *out) {
  if (!s)
    return 0;
  const char *p = STR_DATA(s), *end = p + s->len;
  while (p < end && (*p == ' ' || (*p >= 9 && *p <= 13)))
    p++;
  while (end > p && (end[-1] == ' ' || (end[-1] >= 9 && end[-1] <= 13)))
    end--;
  int neg = 0;
  if (p < end && (*p == '+' || *p == '-')) {
    neg = *p == '-';
    p++;
  }
  if (p == end)
    return 0;
  int64_t v = 0;
  for (; p < end; p++) {
    if (*p < '0' || *p > '9')
      return 0;
    v = v * 10 + (*p - '0');
    if (v > (int64_t)INT32_MAX + 1)
      return 0;
  }
  v = neg ? -v : v;
  if (v < INT32_MIN || v > INT32_MAX)
    return 0;
  *out = (int32_t)v;
  return 1;
}

static int parse_float(const TvmString *s, float *out) {
  if (!s || s->len == 0 || s->len > 200)
    return 0;
  char buf[256];
  memcpy(buf, STR_DATA(s), (size_t)s->len);
  buf[s->len] = '\0';
  char *end;
  double d = strtod(buf, &end);
  while (*end == ' ' || (*end >= 9 && *end <= 13))
    end++;
  if (end == buf || *end)
    return 0;
  *out = (float)d;
  return 1;
}

static TvmStatus int_parse(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (!parse_int(S(a[0]), &r[0].i))
    return tvm_fault_msg(vm, "input string was not in a correct format");
  return TVM_OK;
}

static TvmStatus int_tryparse(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = parse_int(S(a[0]), &r[1].i);
  if (!r[0].i)
    r[1].i = 0;
  return TVM_OK;
}

static TvmStatus float_parse(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (!parse_float(S(a[0]), &r[0].f))
    return tvm_fault_msg(vm, "input string was not in a correct format");
  return TVM_OK;
}

static TvmStatus float_tryparse(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = parse_float(S(a[0]), &r[1].f);
  if (!r[0].i)
    r[1].f = 0.0f;
  return TVM_OK;
}

static TvmStatus io_write(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  (void)r;
  const TvmString *s = S(a[0]);
  if (s)
    fwrite(STR_DATA(s), 1, (size_t)s->len, stdout);
  return TVM_OK;
}

/* ---- char / UTF-8 (TinySystem.Text、runtime/utf8) ---- */

static TvmStatus char_cast(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if ((uint32_t)a[0].i > 255)
    return tvm_fault_msg(vm, "char out of range");
  r[0].i = a[0].i;
  return TVM_OK;
}

static size_t utf8_encode(uint32_t c, char *out) {
  if (c < 0x80) {
    out[0] = (char)c;
    return 1;
  }
  if (c < 0x800) {
    out[0] = (char)(0xC0 | (c >> 6));
    out[1] = (char)(0x80 | (c & 0x3F));
    return 2;
  }
  if (c < 0x10000) {
    out[0] = (char)(0xE0 | (c >> 12));
    out[1] = (char)(0x80 | ((c >> 6) & 0x3F));
    out[2] = (char)(0x80 | (c & 0x3F));
    return 3;
  }
  out[0] = (char)(0xF0 | (c >> 18));
  out[1] = (char)(0x80 | ((c >> 12) & 0x3F));
  out[2] = (char)(0x80 | ((c >> 6) & 0x3F));
  out[3] = (char)(0x80 | (c & 0x3F));
  return 4;
}

static TvmStatus char_from_utf32(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  int32_t c = a[0].i;
  if (c < 0 || c > 0x10FFFF || (c >= 0xD800 && c <= 0xDFFF))
    return tvm_fault_msg(vm, "char.ConvertFromUtf32: not a Unicode scalar value");
  char buf[4];
  return ret_str(vm, r, buf, utf8_encode((uint32_t)c, buf));
}

static TvmStatus rune_count(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  r[0].i = tcs_utf8_rune_count(SDATA(s), (size_t)s->len);
  return TVM_OK;
}

static TvmStatus rune_substring(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  size_t from, to;
  if (!tcs_utf8_rune_range(SDATA(s), (size_t)s->len, a[1].i, a[2].i, &from, &to))
    return tvm_fault_msg(vm, "RuneSubstring: range is out of the string");
  return ret_str(vm, r, STR_DATA(s) + from, to - from);
}

/* foreach (var r in s.EnumerateRunes()): a = (s, 位置) → (次の位置, rune) */
static TvmStatus rune_next(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmString *s = S(a[0]);
  NONNULL_ARG(s, "");
  uint32_t rune;
  size_t at = (size_t)a[1].i;
  at += tcs_utf8_decode(SDATA(s) + at, (size_t)s->len - at, &rune);
  r[0].i = (int32_t)at;
  r[1].i = (int32_t)rune;
  return TVM_OK;
}

/* Utf8Bytes は (s, o, Length) の値。At は byte (範囲外は fault) */
static TvmStatus u8_at(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if ((uint32_t)a[3].i >= (uint32_t)a[2].i)
    return tvm_fault_msg(vm, "index out of range");
  r[0].i = (uint8_t)STR_DATA(S(a[0]))[a[1].i + a[3].i];
  return TVM_OK;
}

static TvmStatus u8_slice(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  int32_t len = a[2].i, start = a[3].i, count = a[4].i;
  if (start < 0 || count < 0 || start > len || count > len - start)
    return tvm_fault_msg(vm, "Utf8Bytes.Slice: range is out of the bytes");
  r[0] = a[0];
  r[1].i = a[1].i + start;
  r[2].i = count;
  return TVM_OK;
}

static TvmStatus u8_tostring(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const unsigned char *p = SDATA(S(a[0])) + a[1].i;
  size_t n = (size_t)a[2].i;
  size_t out_len = tcs_utf8_scrub_length(p, n);
  TvmString *s = tvm_str_new(vm, NULL, 0);
  if (out_len) {
    char *buf = (char *)malloc(out_len);
    if (!buf)
      return oom(vm);
    tcs_utf8_scrub(p, n, (unsigned char *)buf);
    s = tvm_str_new(vm, buf, out_len);
    free(buf);
  }
  if (!s)
    return oom(vm);
  r[0].p = s;
  return TVM_OK;
}

/* ---- Math (il-spec §13: 超越関数は runtime/math、ほかは IEEE-754 で決まる) ---- */

static TvmStatus m_min_f(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  float x = a[0].f, y = a[1].f;
  /* MathF.Min (IEEE 754:2019 minimum): NaN を伝播し、-0 を +0 より小さいとみなす */
  r[0].f = x < y ? x : y < x ? y : (x != x || (x == 0 && signbit(x))) ? x : y;
  return TVM_OK;
}

static TvmStatus m_max_f(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  float x = a[0].f, y = a[1].f;
  r[0].f = x > y ? x : y > x ? y : (x != x || (y == 0 && signbit(y))) ? x : y;
  return TVM_OK;
}

static TvmStatus m_min_i(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = a[0].i < a[1].i ? a[0].i : a[1].i;
  return TVM_OK;
}

static TvmStatus m_max_i(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = a[0].i > a[1].i ? a[0].i : a[1].i;
  return TVM_OK;
}

static TvmStatus m_abs_i(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (a[0].i == INT32_MIN)
    return tvm_fault_msg(vm, "arithmetic overflow");
  r[0].i = a[0].i < 0 ? -a[0].i : a[0].i;
  return TVM_OK;
}

static TvmStatus m_abs_f(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].f = fabsf(a[0].f);
  return TVM_OK;
}

static TvmStatus m_sign_i(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].i = (a[0].i > 0) - (a[0].i < 0);
  return TVM_OK;
}

static TvmStatus m_sign_f(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (a[0].f != a[0].f)
    return tvm_fault_msg(vm, "Function does not accept floating point Not-a-Number values.");
  r[0].i = (a[0].f > 0) - (a[0].f < 0);
  return TVM_OK;
}

static TvmStatus m_clamp_i(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (a[1].i > a[2].i)
    return tvm_fault_msg(vm, "min cannot be greater than max");
  r[0].i = a[0].i < a[1].i ? a[1].i : a[0].i > a[2].i ? a[2].i : a[0].i;
  return TVM_OK;
}

static TvmStatus m_clamp_f(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (a[1].f > a[2].f)
    return tvm_fault_msg(vm, "min cannot be greater than max");
  r[0].f = a[0].f < a[1].f ? a[1].f : a[0].f > a[2].f ? a[2].f : a[0].f;
  return TVM_OK;
}

#define UNARY_F(fname, expr)                                                   \
  static TvmStatus fname(TvmVm *vm, TvmSlot *a, TvmSlot *r) {                  \
    (void)vm;                                                                  \
    float x = a[0].f;                                                          \
    r[0].f = (expr);                                                           \
    return TVM_OK;                                                             \
  }
UNARY_F(m_floor, floorf(x))
UNARY_F(m_ceil, ceilf(x))
UNARY_F(m_sqrt, sqrtf(x))
UNARY_F(m_sin, tcs_sinf(x))
UNARY_F(m_cos, tcs_cosf(x))
UNARY_F(m_tan, tcs_tanf(x))
UNARY_F(m_exp, tcs_expf(x))
UNARY_F(m_log, tcs_logf(x))

static TvmStatus m_atan2(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].f = tcs_atan2f(a[0].f, a[1].f);
  return TVM_OK;
}

static TvmStatus m_pow(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  /* 指数 2 は乗算 (Lua の ^ と tcs2c と同じ) */
  r[0].f = a[1].f == 2.0f ? a[0].f * a[0].f : tcs_powf(a[0].f, a[1].f);
  return TVM_OK;
}

static TvmStatus m_logb(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  float x = a[0].f, b = a[1].f;
  if (b == 1.0f || (x != 1.0f && (b == 0.0f || b == INFINITY)))
    r[0].f = NAN;
  else if (b == 2.0f)
    r[0].f = tcs_log2f(x);
  else if (b == 10.0f)
    r[0].f = tcs_log10f(x);
  else
    r[0].f = tcs_logf(x) / tcs_logf(b);
  return TVM_OK;
}

/* Math.Round: 中間は偶数へ。0 に丸めた結果は x の符号 (Lua の Math.Round と同じ) */
static float round_even(float x, int32_t digits, int has_digits) {
  float scale = 1.0f;
  for (int32_t i = 0; i < digits; i++)
    scale *= 10.0f;
  float scaled = x * scale;
  float fl = floorf(scaled);
  float diff = scaled - fl;
  float rounded;
  if (diff > 0.5f)
    rounded = fl + 1.0f;
  else if (diff < 0.5f)
    rounded = fl;
  else if (fmodf(fl, 2.0f) == 0.0f)
    rounded = fl;
  else
    rounded = fl + 1.0f;
  rounded = copysignf(rounded, x);
  return has_digits ? rounded / scale : rounded;
}

static TvmStatus m_round(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)vm;
  r[0].f = round_even(a[0].f, 0, 0);
  return TVM_OK;
}

static TvmStatus m_round2(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  if (a[1].i < 0 || a[1].i > 6)
    return tvm_fault_msg(vm, "Rounding digits must be between 0 and 6, inclusive.");
  r[0].f = round_even(a[0].f, a[1].i, 1);
  return TVM_OK;
}

/* ---- List ---- */

static TvmStatus list_getrange(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  int32_t index = a[1].i, count = a[2].i;
  if (index < 0 || count < 0 || count > l->count - index)
    return tvm_fault_msg(vm, "Offset and length were out of bounds for the List.");
  TvmList *out = tvm_list_new(vm, l->type);
  if (!out)
    return oom(vm);
  while (out->cap < count)
    if (!tvm_list_grow(vm, out))
      return oom(vm);
  if (count)
    memcpy(out->data, l->data + (size_t)index * l->type->elem_size, (size_t)count * l->type->elem_size);
  out->count = count;
  r[0].p = out;
  return TVM_OK;
}

static TvmStatus list_copy(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  TvmSlot args[3] = {a[0], {0}, {0}};
  args[2].i = l->count;
  return list_getrange(vm, args, r);
}

/* IndexOf (EqualityComparer<T>.Default と同じ等価) */
static int32_t index_of(TvmVm *vm, const TvmList *l, const void *item) {
  for (int32_t i = 0; i < l->count; i++)
    if (tvm_value_eq(vm, l->type->elem_kind, l->type->elem_type, l->data + (size_t)i * l->type->elem_size, item))
      return i;
  return -1;
}

static TvmStatus list_indexof(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  r[0].i = index_of(vm, l, &a[1]);
  return TVM_OK;
}

static TvmStatus list_contains(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  r[0].i = index_of(vm, l, &a[1]) >= 0;
  return TVM_OK;
}

static TvmStatus list_remove(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  TvmList *l = (TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  int32_t i = index_of(vm, l, &a[1]);
  r[0].i = i >= 0;
  if (i >= 0)
    tvm_list_remove_at(l, i);
  return TVM_OK;
}

static TvmSlot load_slot(const char *p, uint8_t kind) {
  TvmSlot s;
  s.u = 0;
  switch (kind) {
  case TVM_K_I32:
    s.i = *(const int32_t *)p;
    break;
  case TVM_K_F32:
    s.f = *(const float *)p;
    break;
  case TVM_K_BOOL:
    s.i = *(const uint8_t *)p;
    break;
  default:
    s.p = *(void *const *)p;
    break;
  }
  return s;
}

static TvmStatus list_findindex(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[0].p;
  NONNULL_ARG(l && a[1].p, "");
  for (int32_t i = 0; i < l->count; i++) {
    TvmSlot arg = load_slot(l->data + (size_t)i * l->type->elem_size, l->type->elem_kind);
    TvmSlot ret;
    TvmStatus st = tvm_call_closure(vm, (const TvmClosure *)a[1].p, &arg, &ret);
    if (st != TVM_OK)
      return st;
    if (ret.i) {
      r[0].i = i;
      return TVM_OK;
    }
  }
  r[0].i = -1;
  return TVM_OK;
}

/* RemoveAll: .NET と同じく述語は各要素に 1 回ずつ先頭から呼び、残す要素の順を保つ。長さは毎回読む */
static TvmStatus list_removeall(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  TvmList *l = (TvmList *)a[0].p;
  NONNULL_ARG(l && a[1].p, "");
  const TvmClosure *pred = (const TvmClosure *)a[1].p;
  size_t es = l->type->elem_size;
  int32_t free_at = 0;
  for (;;) {
    if (free_at >= l->count) {
      r[0].i = 0;
      return TVM_OK;
    }
    TvmSlot arg = load_slot(l->data + (size_t)free_at * es, l->type->elem_kind), ret;
    TvmStatus st = tvm_call_closure(vm, pred, &arg, &ret);
    if (st != TVM_OK)
      return st;
    if (ret.i)
      break;
    free_at++;
  }
  int32_t current = free_at + 1;
  while (current < l->count) {
    while (current < l->count) {
      TvmSlot arg = load_slot(l->data + (size_t)current * es, l->type->elem_kind), ret;
      TvmStatus st = tvm_call_closure(vm, pred, &arg, &ret);
      if (st != TVM_OK)
        return st;
      if (!ret.i)
        break;
      current++;
    }
    if (current < l->count) {
      memcpy(l->data + (size_t)free_at * es, l->data + (size_t)current * es, es);
      free_at++;
      current++;
    }
  }
  int32_t removed = l->count - free_at;
  if (removed < 0) {
    /* 述語が List を縮めた: Lua / tcs2c と同じく数だけを返す (.NET は数えなおすが、どの経路も詰めない) */
    r[0].i = removed;
    return TVM_OK;
  }
  memset(l->data + (size_t)free_at * es, 0, (size_t)removed * es);
  l->count = free_at;
  r[0].i = removed;
  return TVM_OK;
}

static TvmStatus list_reverse(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  TvmList *l = (TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  size_t es = l->type->elem_size;
  char tmp[256];
  if (es > sizeof tmp)
    return tvm_fault_msg(vm, "element too large");
  for (int32_t i = 0, j = l->count - 1; i < j; i++, j--) {
    memcpy(tmp, l->data + (size_t)i * es, es);
    memcpy(l->data + (size_t)i * es, l->data + (size_t)j * es, es);
    memcpy(l->data + (size_t)j * es, tmp, es);
  }
  return TVM_OK;
}

/* 比較なしの Sort (.NET の既定の順): int / float (NaN を先頭に寄せてから残りを並べる)、string (ordinal) */
static int cmp_i32(void *ctx, const void *a, const void *b, int *stop) {
  (void)ctx;
  (void)stop;
  int32_t x = *(const int32_t *)a, y = *(const int32_t *)b;
  return (x > y) - (x < y);
}

static int cmp_f32(void *ctx, const void *a, const void *b, int *stop) {
  (void)ctx;
  (void)stop;
  float x = *(const float *)a, y = *(const float *)b;
  return (x > y) - (x < y);
}

static int cmp_str(void *ctx, const void *a, const void *b, int *stop) {
  (void)ctx;
  (void)stop;
  return tvm_str_compare(*(const TvmString *const *)a, *(const TvmString *const *)b);
}

static TvmStatus list_sort0(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  TvmList *l = (TvmList *)a[0].p;
  NONNULL_ARG(l, "");
  void *scratch[4];
  char *data = l->data;
  size_t n = (size_t)l->count;
  TcsSortCompare cmp;
  switch (l->type->elem_kind) {
  case TVM_K_I32:
    cmp = cmp_i32;
    break;
  case TVM_K_F32: {
    float *v = (float *)data;
    size_t left = 0;
    for (size_t i = 0; i < n; i++)
      if (v[i] != v[i]) {
        float t = v[left];
        v[left] = v[i];
        v[i] = t;
        left++;
      }
    data += left * sizeof(float);
    n -= left;
    cmp = cmp_f32;
    break;
  }
  default:
    if (l->type->elem_kind != TVM_K_REF || l->type->elem_type != vm->string_type)
      return tvm_fault_msg(vm, "Sort without a comparison needs int, float or string elements");
    cmp = cmp_str;
    break;
  }
  tcs_sort(data, n, l->type->elem_size, scratch, cmp, NULL);
  return TVM_OK;
}

/* ---- object / Dictionary / char ---- */

/* object の文字列化: box は中の値 (float は f32 の規則)、string はそのまま、null は空文字列 (il-spec §13) */
static TvmStatus obj_tostring(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmObject *o = (const TvmObject *)a[0].p;
  char buf[32];
  if (!o)
    return ret_str(vm, r, "", 0);
  switch (o->type->tkind) {
  case TVM_T_STRING:
    r[0].p = (void *)o;
    return TVM_OK;
  case TVM_T_BOX: {
    const TvmBox *b = (const TvmBox *)o;
    if (o->type->elem_kind == TVM_K_I32)
      return ret_str(vm, r, buf, (size_t)snprintf(buf, sizeof buf, "%d", b->value.i));
    if (o->type->elem_kind == TVM_K_F32)
      return ret_str(vm, r, buf, tcs_format_f32(b->value.f, buf));
    return b->value.i ? ret_str(vm, r, "True", 4) : ret_str(vm, r, "False", 5);
  }
  default:
    return tvm_fault_msg(vm, "ToString of this value is not supported");
  }
}

static TvmStatus dict_clear(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  TvmDict *d = (TvmDict *)a[0].p;
  NONNULL_ARG(d, "");
  if (d->entries)
    memset(d->entries, 0, (size_t)d->cap * d->type->dict_entry);
  if (d->buckets)
    memset(d->buckets, 0, (size_t)d->nbuckets * sizeof(int32_t));
  d->count = d->used = d->free = 0;
  return TVM_OK;
}

#define CHAR_PRED(fname, expr)                                                 \
  static TvmStatus fname(TvmVm *vm, TvmSlot *a, TvmSlot *r) {                  \
    (void)vm;                                                                  \
    int32_t c = a[0].i;                                                        \
    r[0].i = (expr);                                                           \
    return TVM_OK;                                                             \
  }
/* char は 1 byte の code unit (tinysystem.lua の Char と同じ ASCII の判定) */
CHAR_PRED(ch_digit, c >= 48 && c <= 57)
CHAR_PRED(ch_upper, c >= 65 && c <= 90)
CHAR_PRED(ch_lower, c >= 97 && c <= 122)
CHAR_PRED(ch_letter, (c >= 65 && c <= 90) || (c >= 97 && c <= 122))
CHAR_PRED(ch_letter_digit, (c >= 65 && c <= 90) || (c >= 97 && c <= 122) || (c >= 48 && c <= 57))
CHAR_PRED(ch_white, c == 32 || (c >= 9 && c <= 13))
CHAR_PRED(ch_toupper, c >= 97 && c <= 122 ? c - 32 : c)
CHAR_PRED(ch_tolower, c >= 65 && c <= 90 ? c + 32 : c)

static TvmStatus str_joinenum(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  const TvmList *l = (const TvmList *)a[1].p;
  NONNULL_ARG(l, "");
  /* 要素ごとに FormatEnum した string の List を作って Join に渡す */
  TvmType *lt = NULL;
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (vm->types[i].tkind == TVM_T_LIST && vm->types[i].elem_kind == TVM_K_REF &&
        vm->types[i].elem_type == vm->string_type)
      lt = &vm->types[i];
  if (!lt)
    return tvm_fault_msg(vm, "JoinEnum needs List<string> in the module");
  TvmList *parts = tvm_list_new(vm, lt);
  if (!parts)
    return oom(vm);
  vm->pins[vm->npins++] = (void **)&parts;
  TvmStatus st = TVM_OK;
  for (int32_t i = 0; i < l->count && st == TVM_OK; i++) {
    TvmSlot args[2], ret[1];
    args[0].i = ((const int32_t *)l->data)[i];
    args[1] = a[2];
    st = fmt_enum(vm, args, ret);
    if (st == TVM_OK) {
      if (parts->count == parts->cap && !tvm_list_grow(vm, parts))
        st = oom(vm);
      else
        ((void **)parts->data)[parts->count++] = ret[0].p;
    }
  }
  if (st == TVM_OK) {
    TvmSlot args[2];
    args[0] = a[0];
    args[1].p = parts;
    st = str_join(vm, args, r);
  }
  vm->npins--;
  return st;
}

/* ---- Random (il-spec §13): Lua 5.5 の math.random (xoshiro256**、LUA_32BITS 構成) と同じ列。
   tcs2c の tcs_rand_* と同じ手順。instance は 8 個の int の葉 (64 bit × 4) を持つ object ---- */

#include <time.h>

static uint64_t *rand_state(void *o) { return (uint64_t *)((char *)o + sizeof(TvmObject)); }

static uint64_t rotl64(uint64_t x, int n) { return (x << n) | (x >> (64 - n)); }

static uint64_t rand_next(uint64_t *s) {
  uint64_t s0 = s[0], s1 = s[1], s2 = s[2] ^ s0, s3 = s[3] ^ s1;
  uint64_t res = rotl64(s1 * 5, 7) * 9;
  s[0] = s0 ^ s3;
  s[1] = s1 ^ s2;
  s[2] = s2 ^ (s1 << 17);
  s[3] = rotl64(s3, 45);
  return res;
}

static void rand_seed(uint64_t *s, uint32_t n1, uint32_t n2) {
  s[0] = n1;
  s[1] = 0xff;
  s[2] = n2;
  s[3] = 0;
  for (int i = 0; i < 16; i++)
    rand_next(s);
}

static uint32_t rand_auto_counter;

static uint32_t rand_auto_seed(void) {
  rand_auto_counter++;
  return (uint32_t)time(NULL) ^ (uint32_t)(uintptr_t)&rand_auto_counter ^ (rand_auto_counter * 0x9E3779B1u);
}

static uint32_t rand_project(uint64_t *s, uint32_t ran, uint32_t n) {
  uint32_t lim = n;
  for (int sh = 1; (lim & (lim + 1)) != 0; sh *= 2)
    lim |= (lim >> sh);
  while ((ran &= lim) > n)
    ran = (uint32_t)rand_next(s);
  return ran;
}

static TvmStatus rand_range_(TvmVm *vm, uint64_t *s, int32_t low, int32_t up, int32_t *out) {
  uint64_t rv = rand_next(s);
  if (low > up)
    return tvm_fault_msg(vm, "interval is empty");
  uint32_t p = rand_project(s, (uint32_t)rv, (uint32_t)up - (uint32_t)low);
  *out = (int32_t)(p + (uint32_t)low);
  return TVM_OK;
}

static TvmStatus random_seed(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  NONNULL_ARG(a[0].p, "");
  rand_seed(rand_state(a[0].p), (uint32_t)a[1].i, 0);
  return TVM_OK;
}

static TvmStatus random_autoseed(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  (void)r;
  NONNULL_ARG(a[0].p, "");
  rand_seed(rand_state(a[0].p), rand_auto_seed(), 0);
  return TVM_OK;
}

/* Next(min, max): 上限は排他。空の範囲 (max == min) は .NET と同じく min */
static TvmStatus random_next(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  NONNULL_ARG(a[0].p, "");
  int32_t min = a[1].i, max = a[2].i;
  return rand_range_(vm, rand_state(a[0].p), min, max == min ? min : max - 1, &r[0].i);
}

static TvmStatus random_range(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  NONNULL_ARG(a[0].p, "");
  return rand_range_(vm, rand_state(a[0].p), a[1].i, a[2].i, &r[0].i);
}

/* 上位 24 bit から [0, 1) の float */
static TvmStatus random_float(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  NONNULL_ARG(a[0].p, "");
  uint64_t x = rand_next(rand_state(a[0].p));
  int64_t sx = (int64_t)(x >> 40);
  float res = (float)sx * (0.5f / (float)(1 << 23));
  if (sx < 0)
    res += 1.0f;
  r[0].f = res;
  return TVM_OK;
}

/* ---- HashSet<T> (Dictionary<T, bool> の上。.NET の HashSet と同じく null を入れられ、列挙は消した場所を使い回す順) ---- */

static TvmStatus set_add(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  TvmDict *d = (TvmDict *)a[0].p;
  NONNULL_ARG(d, "");
  if (tvm_dict_find(vm, d, a[1]) >= 0) {
    r[0].i = 0;
    return TVM_OK;
  }
  if (tvm_dict_insert(vm, d, a[1]) < 0)
    return oom(vm);
  r[0].i = 1;
  return TVM_OK;
}

static TvmStatus set_contains(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  TvmDict *d = (TvmDict *)a[0].p;
  NONNULL_ARG(d, "");
  r[0].i = tvm_dict_find(vm, d, a[1]) >= 0;
  return TVM_OK;
}

static TvmStatus set_remove(TvmVm *vm, TvmSlot *a, TvmSlot *r) {
  TvmDict *d = (TvmDict *)a[0].p;
  NONNULL_ARG(d, "");
  r[0].i = tvm_dict_remove(vm, d, a[1]);
  return TVM_OK;
}

/* ---- 表 ---- */

const TvmIntrinsic tvm_intrinsics[] = {
    {"string.formatfloat", "fs", "s", fmt_float},
    {"string.formatint", "is", "s", fmt_int},
    {"string.formatenum", "is", "s", fmt_enum},
    {"string.trim", "s", "s", str_trim},
    {"string.replace", "sss", "s", str_replace},
    {"string.contains", "ss", "b", str_contains},
    {"string.startswith", "ss", "b", str_starts},
    {"string.endswith", "ss", "b", str_ends},
    {"string.isnullorempty", "s", "b", str_empty},
    {"string.compareordinal", "ss", "i", str_compare},
    {"string.join", "sL", "s", str_join},
    {"string.split", "Lss", "", str_split},
    {"string.splitws", "Ls", "", str_split_ws},
    {"string.toupper", "s", "s", str_upper},
    {"string.tolower", "s", "s", str_lower},
    {"string.bytelen", "s", "i", str_bytelen},
    {"int.parse", "s", "i", int_parse},
    {"int.tryparse", "s", "bi", int_tryparse},
    {"float.parse", "s", "f", float_parse},
    {"float.tryparse", "s", "bf", float_tryparse},
    {"io.write", "s", "", io_write},
    {"char.cast", "i", "i", char_cast},
    {"char.fromutf32", "i", "s", char_from_utf32},
    {"utf8.runecount", "s", "i", rune_count},
    {"utf8.runesubstring", "sii", "s", rune_substring},
    {"utf8.runenext", "si", "ii", rune_next},
    {"utf8.at", "siii", "i", u8_at},
    {"utf8.slice", "siiii", "sii", u8_slice},
    {"utf8.tostring", "sii", "s", u8_tostring},
    {"math.min.f", "ff", "f", m_min_f},
    {"math.max.f", "ff", "f", m_max_f},
    {"math.min.i", "ii", "i", m_min_i},
    {"math.max.i", "ii", "i", m_max_i},
    {"math.abs.i", "i", "i", m_abs_i},
    {"math.abs.f", "f", "f", m_abs_f},
    {"math.sign.i", "i", "i", m_sign_i},
    {"math.sign.f", "f", "i", m_sign_f},
    {"math.clamp.i", "iii", "i", m_clamp_i},
    {"math.clamp.f", "fff", "f", m_clamp_f},
    {"math.floor", "f", "f", m_floor},
    {"math.ceiling", "f", "f", m_ceil},
    {"math.sqrt", "f", "f", m_sqrt},
    {"math.sin", "f", "f", m_sin},
    {"math.cos", "f", "f", m_cos},
    {"math.tan", "f", "f", m_tan},
    {"math.exp", "f", "f", m_exp},
    {"math.log", "f", "f", m_log},
    {"math.logb", "ff", "f", m_logb},
    {"math.atan2", "ff", "f", m_atan2},
    {"math.pow", "ff", "f", m_pow},
    {"math.round", "f", "f", m_round},
    {"math.round2", "fi", "f", m_round2},
    {"list.getrange", "Lii", "r", list_getrange},
    {"list.copy", "L", "r", list_copy},
    {"list.indexof", "Le", "i", list_indexof},
    {"list.contains", "Le", "b", list_contains},
    {"list.remove", "Le", "b", list_remove},
    {"list.findindex", "LF", "i", list_findindex},
    {"list.removeall", "LF", "i", list_removeall},
    {"list.reverse", "L", "", list_reverse},
    {"list.sort0", "L", "", list_sort0},
    {"object.tostring", "o", "s", obj_tostring},
    {"dict.clear", "D", "", dict_clear},
    {"char.isdigit", "i", "b", ch_digit},
    {"char.isupper", "i", "b", ch_upper},
    {"char.islower", "i", "b", ch_lower},
    {"char.isletter", "i", "b", ch_letter},
    {"char.isletterordigit", "i", "b", ch_letter_digit},
    {"char.iswhitespace", "i", "b", ch_white},
    {"char.toupper", "i", "i", ch_toupper},
    {"char.tolower", "i", "i", ch_tolower},
    {"string.joinenum", "sLs", "s", str_joinenum},
    {"random.seed", "oi", "", random_seed},
    {"random.autoseed", "o", "", random_autoseed},
    {"random.next", "oii", "i", random_next},
    {"random.range", "oii", "i", random_range},
    {"random.float", "o", "f", random_float},
    {"set.add", "Dk", "b", set_add},
    {"set.contains", "Dk", "b", set_contains},
    {"set.remove", "Dk", "b", set_remove},
};

const size_t tvm_nintrinsics = sizeof tvm_intrinsics / sizeof tvm_intrinsics[0];
