/* tcs_model.h の実装 (doc/object-model.md §4、§5)。 */
#include "tcs_model.h"

#include <stdlib.h>
#include <string.h>

TCS_MODEL_API uint8_t tcs_rd_u8(TcsReader *r) {
  if (r->end - r->p < 1) {
    r->bad = 1;
    return 0;
  }
  return *r->p++;
}

TCS_MODEL_API uint16_t tcs_rd_u16(TcsReader *r) {
  if (r->end - r->p < 2) {
    r->bad = 1;
    return 0;
  }
  uint16_t v = (uint16_t)(r->p[0] | (r->p[1] << 8));
  r->p += 2;
  return v;
}

TCS_MODEL_API uint32_t tcs_rd_u32(TcsReader *r) {
  if (r->end - r->p < 4) {
    r->bad = 1;
    return 0;
  }
  uint32_t v = (uint32_t)r->p[0] | ((uint32_t)r->p[1] << 8) | ((uint32_t)r->p[2] << 16) |
               ((uint32_t)r->p[3] << 24);
  r->p += 4;
  return v;
}

TCS_MODEL_API int tcs_rd_count_ok(TcsReader *r, uint32_t n, size_t unit) {
  if ((size_t)(r->end - r->p) / (unit ? unit : 1) < n) {
    r->bad = 1;
    return 0;
  }
  return 1;
}

static TcsInfo *rd_infos(TcsReader *r, uint16_t n) {
  if (!tcs_rd_count_ok(r, n, 3))
    return NULL;
  TcsInfo *a = (TcsInfo *)calloc(n ? n : 1, sizeof(TcsInfo));
  for (uint32_t k = 0; k < n && a; k++) {
    a[k].kind = tcs_rd_u8(r);
    a[k].type = tcs_rd_u16(r);
  }
  return a;
}

TCS_MODEL_API const char *tcs_model_read(TcsReader *r, const char *const *strings, uint32_t nstrings, uint32_t *ntypes,
                           TcsType **types) {
  uint32_t n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 10))
    return "truncated type table";
  TcsType *ts = (TcsType *)calloc(n ? n : 1, sizeof(TcsType));
  if (!ts)
    return "out of memory";
  *types = ts;
  *ntypes = n;
  for (uint32_t i = 0; i < n && !r->bad; i++) {
    TcsType *t = &ts[i];
    t->tkind = tcs_rd_u8(r);
    uint32_t name = tcs_rd_u32(r);
    t->name = name < nstrings ? strings[name] : "?";
    t->layout_hash = tcs_rd_u32(r);
    t->flags = tcs_rd_u8(r);
    switch (t->tkind) {
    case TCS_T_CLASS:
    case TCS_T_STRUCT:
      t->nfields = tcs_rd_u16(r);
      if (!tcs_rd_count_ok(r, t->nfields, 7))
        return "truncated type table";
      t->fields = (TcsField *)calloc(t->nfields ? t->nfields : 1, sizeof(TcsField));
      if (!t->fields)
        return "out of memory";
      for (uint32_t k = 0; k < t->nfields; k++) {
        uint32_t fname = tcs_rd_u32(r);
        t->fields[k].name = fname < nstrings ? strings[fname] : "?";
        t->fields[k].kind = tcs_rd_u8(r);
        t->fields[k].type = tcs_rd_u16(r);
      }
      if (t->tkind == TCS_T_CLASS) {
        t->nifaces = tcs_rd_u16(r);
        if (!tcs_rd_count_ok(r, t->nifaces, 2))
          return "truncated type table";
        t->ifaces = (uint16_t *)calloc(t->nifaces ? t->nifaces : 1, sizeof(uint16_t));
        for (uint32_t k = 0; k < t->nifaces && t->ifaces; k++)
          t->ifaces[k] = tcs_rd_u16(r);
        t->nitable = tcs_rd_u16(r);
        if (!tcs_rd_count_ok(r, t->nitable, 4))
          return "truncated type table";
        t->itable = (TcsItable *)calloc(t->nitable ? t->nitable : 1, sizeof(TcsItable));
        for (uint32_t k = 0; k < t->nitable && t->itable; k++) {
          t->itable[k].selector = tcs_rd_u16(r);
          t->itable[k].func = tcs_rd_u16(r);
        }
        if (!t->ifaces || !t->itable)
          return "out of memory";
      }
      break;
    case TCS_T_ARRAY:
    case TCS_T_LIST:
      t->elem_kind = tcs_rd_u8(r);
      t->elem_type = tcs_rd_u16(r);
      break;
    case TCS_T_DICT:
      t->key_kind = tcs_rd_u8(r);
      t->key_type = tcs_rd_u16(r);
      t->elem_kind = tcs_rd_u8(r);
      t->elem_type = tcs_rd_u16(r);
      break;
    case TCS_T_FUNC:
      t->nparams = tcs_rd_u16(r);
      t->params = rd_infos(r, t->nparams);
      t->nrets = tcs_rd_u16(r);
      t->rets = rd_infos(r, t->nrets);
      if (!t->params || !t->rets)
        return "truncated type table";
      break;
    case TCS_T_BOX:
      t->elem_kind = tcs_rd_u8(r);
      break;
    case TCS_T_STRING:
    case TCS_T_IFACE:
    case TCS_T_OBJECT:
      break;
    default:
      return "bad type kind";
    }
  }
  return r->bad ? "truncated type table" : NULL;
}

TCS_MODEL_API uint32_t tcs_kind_size(uint8_t kind, uint32_t ref_size) {
  switch (kind) {
  case TCS_K_I32:
  case TCS_K_F32:
    return 4;
  case TCS_K_BOOL:
    return 1;
  default:
    return ref_size;
  }
}

static uint32_t align_up(uint32_t v, uint32_t a) { return (v + a - 1) / a * a; }

/* 型 i の layout。struct は field の struct を先に (state: 0 未着手、1 計算中、2 済み)。循環は誤り */
static const char *layout_one(TcsType *ts, uint32_t n, uint32_t i, uint8_t *state, uint32_t class_prefix,
                              uint32_t ref_size) {
  TcsType *t = &ts[i];
  if (state[i] == 2)
    return NULL;
  if (state[i] == 1)
    return "struct contains itself";
  state[i] = 1;
  if (t->tkind == TCS_T_CLASS || t->tkind == TCS_T_STRUCT) {
    uint32_t off = t->tkind == TCS_T_CLASS ? class_prefix : 0, align = 1, nleaves = 0;
    for (uint32_t k = 0; k < t->nfields; k++) {
      TcsField *f = &t->fields[k];
      uint32_t s, a;
      if (f->kind == TCS_K_STRUCT) {
        if (f->type >= n || ts[f->type].tkind != TCS_T_STRUCT)
          return "bad struct field";
        const char *err = layout_one(ts, n, f->type, state, class_prefix, ref_size);
        if (err)
          return err;
        s = ts[f->type].size;
        a = ts[f->type].align;
        nleaves += ts[f->type].nleaves;
      } else {
        if (f->kind > TCS_K_REF || (f->kind == TCS_K_REF && f->type != TCS_NO_TYPE && f->type >= n))
          return "bad field";
        s = a = tcs_kind_size(f->kind, ref_size);
        nleaves++;
      }
      off = align_up(off, a);
      f->offset = off;
      off += s;
      if (a > align)
        align = a;
      if (off > 0xFFFF)
        return "type too large";
    }
    t->align = align;
    if (t->tkind == TCS_T_CLASS) {
      uint32_t ca = align > ref_size ? align : ref_size;
      t->size = align_up(off ? off : 1, ca);
    } else {
      t->size = off ? align_up(off, align) : 1; /* 空の struct は 1 byte (C# と同じ) */
    }
    if (nleaves > 0xFFFF)
      return "type too large";
    /* 葉: field を struct まで展開 (宣言の順、深さ優先) */
    t->nleaves = (uint16_t)nleaves;
    t->leaves = (TcsLeaf *)calloc(nleaves ? nleaves : 1, sizeof(TcsLeaf));
    if (!t->leaves)
      return "out of memory";
    uint32_t at = 0;
    for (uint32_t k = 0; k < t->nfields; k++) {
      TcsField *f = &t->fields[k];
      if (f->kind == TCS_K_STRUCT) {
        const TcsType *st = &ts[f->type];
        for (uint32_t j = 0; j < st->nleaves; j++) {
          t->leaves[at] = st->leaves[j];
          t->leaves[at].offset += f->offset;
          at++;
        }
      } else {
        t->leaves[at].kind = f->kind;
        t->leaves[at].type = f->type;
        t->leaves[at].offset = f->offset;
        at++;
      }
    }
  }
  state[i] = 2;
  return NULL;
}

TCS_MODEL_API const char *tcs_model_layout(TcsType *ts, uint32_t n, uint32_t class_prefix, uint32_t ref_size) {
  uint8_t *state = (uint8_t *)calloc(n ? n : 1, 1);
  if (!state)
    return "out of memory";
  const char *err = NULL;
  for (uint32_t i = 0; i < n && !err; i++)
    err = layout_one(ts, n, i, state, class_prefix, ref_size);
  free(state);
  if (err)
    return err;
  for (uint32_t i = 0; i < n; i++) {
    TcsType *t = &ts[i];
    if (t->tkind != TCS_T_ARRAY && t->tkind != TCS_T_LIST && t->tkind != TCS_T_DICT)
      continue;
    if (t->elem_kind == TCS_K_STRUCT) {
      if (t->elem_type >= n || ts[t->elem_type].tkind != TCS_T_STRUCT)
        return "bad struct element";
      t->elem_size = ts[t->elem_type].size;
    } else if (t->elem_kind <= TCS_K_REF) {
      if (t->elem_kind == TCS_K_REF && t->elem_type != TCS_NO_TYPE && t->elem_type >= n)
        return "bad element type";
      t->elem_size = tcs_kind_size(t->elem_kind, ref_size);
    } else {
      return "bad element kind";
    }
  }
  return NULL;
}

TCS_MODEL_API void tcs_model_free(TcsType *ts, uint32_t n) {
  if (!ts)
    return;
  for (uint32_t i = 0; i < n; i++) {
    free(ts[i].fields);
    free(ts[i].leaves);
    free(ts[i].params);
    free(ts[i].rets);
    free(ts[i].ifaces);
    free(ts[i].itable);
  }
  free(ts);
}

TCS_MODEL_API const char *tcs_model_read_strings(TcsReader *r, char ***strings, uint32_t *n) {
  uint32_t count = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, count, 4))
    return "truncated string table";
  char **list = (char **)calloc(count ? count : 1, sizeof(char *));
  if (!list)
    return "out of memory";
  *strings = list;
  *n = count;
  for (uint32_t i = 0; i < count && !r->bad; i++) {
    uint32_t len = tcs_rd_u32(r);
    if (!tcs_rd_count_ok(r, len, 1))
      return "truncated string table";
    list[i] = (char *)malloc(len + 1);
    if (!list[i])
      return "out of memory";
    memcpy(list[i], r->p, len);
    list[i][len] = '\0';
    r->p += len;
  }
  return r->bad ? "truncated string table" : NULL;
}

TCS_MODEL_API void tcs_model_free_strings(char **strings, uint32_t n) {
  for (uint32_t i = 0; strings && i < n; i++)
    free(strings[i]);
  free(strings);
}
