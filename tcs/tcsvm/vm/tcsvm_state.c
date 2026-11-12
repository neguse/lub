/* 状態の保存と読み込み (doc/vm-design.md §13.4)。
   static と、そこから届く object を、address と layout に依らない byte 列に書く。
   読む側は同じ形の型を持つ別の VM (別の process でもよい) で、object を作り直して static に入れる。

   byte 列 (little endian):
     u32 magic、u32 版
     型の表: u32 数、型ごとに 名前、u8 種類、形
       class / struct: u8 host の値を持つか、u32 field の数、field ごとに 名前、u8 種類、u32 struct の型 (struct の field だけ)
       配列 / List / box: u8 要素の種類、u32 要素の struct の型
       Dictionary: u8 key の種類、u8 値の種類、u32 値の struct の型
     object の頭: u32 数、object ごとに u32 型、u32 数 (配列と List と Dictionary は要素、string は byte、closure は捕捉)、
       string は byte 列、closure は関数の名前
     object の中身: 頭と同じ順
     static: u32 数、static ごとに 名前、u8 種類、u32 struct の型、値
   値は int と float が 4 byte、bool が 1 byte、参照が u32 (object の番号 + 1。0 は null)、struct は field の順。
   名前は u32 の長さと byte 列。

   読み込みは、型の形が今の module と同じであること、参照の先が置き場所の型に合うこと、closure の関数が関数型に合うことを確かめる。
   確かめ終わるまで static に触らないので、断ったときは状態は変わらない。
   host の handle が持つ object は書かない (読み込んだ後も handle は前の object を指す)。 */
#include "tcsvm_internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define STATE_MAGIC 0x53534354u /* "TCSS" */
#define STATE_VERSION 1u
#define NONE 0xFFFFFFFFu
#define NAME_MAX_LEN 1024u

/* ---- 書く ---- */

typedef struct Buf {
  uint8_t *p;
  size_t n, cap;
  bool failed;
} Buf;

static void put(Buf *b, const void *data, size_t n) {
  if (b->failed)
    return;
  if (b->n + n > b->cap) {
    size_t cap = b->cap ? b->cap * 2 : 1024;
    while (cap < b->n + n)
      cap *= 2;
    uint8_t *p = (uint8_t *)realloc(b->p, cap);
    if (!p) {
      b->failed = true;
      return;
    }
    b->p = p;
    b->cap = cap;
  }
  if (n)
    memcpy(b->p + b->n, data, n);
  b->n += n;
}

static void put_u8(Buf *b, uint8_t v) { put(b, &v, 1); }

static void put_u32(Buf *b, uint32_t v) {
  uint8_t x[4] = {(uint8_t)v, (uint8_t)(v >> 8), (uint8_t)(v >> 16),
                  (uint8_t)(v >> 24)};
  put(b, x, 4);
}

static void put_u64(Buf *b, uint64_t v) {
  put_u32(b, (uint32_t)v);
  put_u32(b, (uint32_t)(v >> 32));
}

static void put_f32(Buf *b, float f) {
  uint32_t bits;
  memcpy(&bits, &f, 4);
  put_u32(b, bits);
}

static void put_str(Buf *b, const char *s) {
  size_t n = strlen(s);
  put_u32(b, (uint32_t)n);
  put(b, s, n);
}

typedef struct Save {
  TvmVm *vm;
  Buf heads, bodies, statics, types;
  uint32_t *type_at; /* VM の型 → 状態の型の番号 (NONE はまだ) */
  uint16_t *type_of; /* 状態の型の番号 → VM の型 */
  uint32_t ntypes;
  void **objs; /* 番号 → object (見つけた順。後ろへ積み、前から書く) */
  uint32_t nobjs, capobjs;
  void **seen_keys; /* object → 番号 (open addressing) */
  uint32_t *seen_vals;
  uint32_t seen_cap;
  const char *error;
  char error_buf[256];
} Save;

static uint32_t save_type(Save *sv, uint16_t t) {
  if (sv->type_at[t] == NONE) {
    sv->type_at[t] = sv->ntypes;
    sv->type_of[sv->ntypes++] = t;
  }
  return sv->type_at[t];
}

static uint32_t ptr_hash(const void *p) {
  uintptr_t x = (uintptr_t)p >> 4;
  return (uint32_t)(x ^ (x >> 16)) * 2654435761u;
}

static bool seen_grow(Save *sv) {
  uint32_t cap = sv->seen_cap ? sv->seen_cap * 2 : 1024;
  void **keys = (void **)calloc(cap, sizeof(void *));
  uint32_t *vals = (uint32_t *)malloc(cap * sizeof(uint32_t));
  if (!keys || !vals) {
    free(keys);
    free(vals);
    return false;
  }
  for (uint32_t i = 0; i < sv->seen_cap; i++) {
    if (!sv->seen_keys[i])
      continue;
    uint32_t k = ptr_hash(sv->seen_keys[i]) & (cap - 1);
    while (keys[k])
      k = (k + 1) & (cap - 1);
    keys[k] = sv->seen_keys[i];
    vals[k] = sv->seen_vals[i];
  }
  free(sv->seen_keys);
  free(sv->seen_vals);
  sv->seen_keys = keys;
  sv->seen_vals = vals;
  sv->seen_cap = cap;
  return true;
}

/* 参照の値: object の番号 + 1 (初めて見た object は列の後ろへ積む) */
static uint32_t save_ref(Save *sv, void *p) {
  if (!p || sv->error)
    return 0;
  if (GC_BLOB(GC_HEADER(p)) || !TVM_TYPE(p)) {
    sv->error = "a reference points to a host buffer";
    return 0;
  }
  if ((sv->nobjs + 1) * 2 > sv->seen_cap && !seen_grow(sv)) {
    sv->error = "out of memory";
    return 0;
  }
  uint32_t k = ptr_hash(p) & (sv->seen_cap - 1);
  while (sv->seen_keys[k]) {
    if (sv->seen_keys[k] == p)
      return sv->seen_vals[k] + 1;
    k = (k + 1) & (sv->seen_cap - 1);
  }
  if (sv->nobjs == sv->capobjs) {
    uint32_t cap = sv->capobjs ? sv->capobjs * 2 : 1024;
    void **objs = (void **)realloc(sv->objs, cap * sizeof(void *));
    if (!objs) {
      sv->error = "out of memory";
      return 0;
    }
    sv->objs = objs;
    sv->capobjs = cap;
  }
  sv->seen_keys[k] = p;
  sv->seen_vals[k] = sv->nobjs;
  sv->objs[sv->nobjs++] = p;
  return sv->nobjs;
}

/* register か static の 1 値 */
static void save_slot(Save *sv, Buf *b, uint8_t kind, TvmSlot s) {
  switch (kind) {
  case TVM_K_I32:
    put_u32(b, (uint32_t)s.i);
    break;
  case TVM_K_F32:
    put_f32(b, s.f);
    break;
  case TVM_K_BOOL:
    put_u8(b, s.i ? 1 : 0);
    break;
  default:
    put_u32(b, save_ref(sv, s.p));
    break;
  }
}

/* object の中の値 (field、要素) */
static void save_value(Save *sv, Buf *b, uint8_t kind, uint16_t type,
                       const char *mem) {
  switch (kind) {
  case TVM_K_I32:
  case TVM_K_F32: {
    uint32_t v;
    memcpy(&v, mem, 4);
    put_u32(b, v);
    break;
  }
  case TVM_K_BOOL:
    put_u8(b, *(const uint8_t *)mem ? 1 : 0);
    break;
  case TVM_K_REF: {
    void *p;
    memcpy(&p, mem, sizeof p);
    put_u32(b, save_ref(sv, p));
    break;
  }
  default: {
    const TvmType *st = &sv->vm->types[type];
    save_type(sv, type);
    for (uint32_t k = 0; k < st->nfields; k++)
      save_value(sv, b, st->fields[k].kind, st->fields[k].type,
                 mem + st->fields[k].offset);
    break;
  }
  }
}

static void save_object(Save *sv, void *p) {
  TvmVm *vm = sv->vm;
  const TvmType *t = TVM_TYPE(p);
  Buf *h = &sv->heads, *b = &sv->bodies;
  put_u32(h, save_type(sv, (uint16_t)(t - vm->types)));
  switch (t->tkind) {
  case TVM_T_CLASS:
    put_u32(h, 0);
    for (uint32_t k = 0; k < t->nfields; k++)
      save_value(sv, b, t->fields[k].kind, t->fields[k].type,
                 (const char *)p + t->fields[k].offset);
    if (t->flags & TCS_TYPE_HOST_VALUE)
      put_u64(b, TVM_HOST_VALUE(p));
    break;
  case TVM_T_ARRAY: {
    const TvmArray *a = (const TvmArray *)p;
    put_u32(h, (uint32_t)a->len);
    for (int32_t i = 0; i < a->len; i++)
      save_value(sv, b, t->elem_kind, t->elem_type,
                 ARR_DATA(a) + (size_t)i * t->elem_size);
    break;
  }
  case TVM_T_STRING: {
    const TvmString *s = (const TvmString *)p;
    put_u32(h, (uint32_t)s->len);
    put(h, STR_DATA(s), (size_t)s->len);
    break;
  }
  case TVM_T_LIST: {
    const TvmList *l = (const TvmList *)p;
    put_u32(h, (uint32_t)l->count);
    for (int32_t i = 0; i < l->count; i++)
      save_value(sv, b, t->elem_kind, t->elem_type,
                 l->data + (size_t)i * t->elem_size);
    break;
  }
  case TVM_T_DICT: {
    const TvmDict *d = (const TvmDict *)p;
    put_u32(h, (uint32_t)d->count);
    for (int32_t i = 0; i < d->used; i++) {
      const TvmDictEntry *e = DICT_ENTRY(d, i);
      if (!e->live)
        continue;
      save_slot(sv, b, t->key_kind, e->key);
      save_value(sv, b, t->elem_kind, t->elem_type, DICT_VALUE(e));
    }
    break;
  }
  case TVM_T_FUNC: {
    const TvmClosure *c = (const TvmClosure *)p;
    put_u32(h, c->ncaps);
    put_str(h, c->proto->name);
    for (uint32_t k = 0; k < c->ncaps; k++)
      save_slot(sv, b, c->proto->caps[k].kind, CLO_CAPS(c)[k]);
    break;
  }
  case TVM_T_BOX:
    put_u32(h, 0);
    save_slot(sv, b, t->elem_kind, ((const TvmBox *)p)->value);
    break;
  default:
    sv->error = "an object has a type that cannot be saved";
    break;
  }
}

static void save_type_shape(Save *sv, uint32_t i) {
  const TvmType *t = &sv->vm->types[sv->type_of[i]];
  Buf *b = &sv->types;
  put_str(b, t->name);
  put_u8(b, t->tkind);
  switch (t->tkind) {
  case TVM_T_CLASS:
  case TVM_T_STRUCT:
    put_u8(b, t->flags & TCS_TYPE_HOST_VALUE ? 1 : 0);
    put_u32(b, t->nfields);
    for (uint32_t k = 0; k < t->nfields; k++) {
      put_str(b, t->fields[k].name);
      put_u8(b, t->fields[k].kind);
      put_u32(b, t->fields[k].kind == TVM_K_STRUCT
                     ? save_type(sv, t->fields[k].type)
                     : NONE);
    }
    break;
  case TVM_T_DICT:
    put_u8(b, t->key_kind);
    /* fallthrough */
  case TVM_T_ARRAY:
  case TVM_T_LIST:
  case TVM_T_BOX:
    put_u8(b, t->elem_kind);
    put_u32(b,
            t->elem_kind == TVM_K_STRUCT ? save_type(sv, t->elem_type) : NONE);
    break;
  default:
    break;
  }
}

static bool at_frame_boundary(TvmVm *vm, const char *what) {
  if (!vm->nfuncs) {
    tvm_set_error(vm, "%s needs a loaded module", what);
    return false;
  }
  if (vm->ftop != vm->frames || vm->depth || vm->npins || vm->nstruct_pins) {
    tvm_set_error(vm, "%s runs only at a frame boundary", what);
    return false;
  }
  if (vm->heap) {
    tvm_set_error(vm, "%s does not support a shared heap", what);
    return false;
  }
  return true;
}

TvmStatus tvm_state_save(TvmVm *vm, uint8_t **bytes, size_t *size) {
  *bytes = NULL;
  *size = 0;
  vm->error[0] = '\0';
  if (!at_frame_boundary(vm, "tvm_state_save"))
    return TVM_LOAD_ERROR;
  Save sv = {0};
  sv.vm = vm;
  uint32_t nt = vm->ntypes ? vm->ntypes : 1;
  sv.type_at = (uint32_t *)malloc(nt * sizeof(uint32_t));
  sv.type_of = (uint16_t *)malloc(nt * sizeof(uint16_t));
  if (!sv.type_at || !sv.type_of)
    sv.error = "out of memory";
  else
    for (uint32_t i = 0; i < nt; i++)
      sv.type_at[i] = NONE;
  if (!sv.error) {
    put_u32(&sv.statics, vm->nstatic_fields);
    for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
      const TvmStaticField *f = &vm->static_fields[i];
      put_str(&sv.statics, f->name);
      put_u8(&sv.statics, f->kind);
      if (f->kind != TVM_K_STRUCT) {
        put_u32(&sv.statics, NONE);
        save_slot(&sv, &sv.statics, f->kind, vm->statics[f->slot]);
        continue;
      }
      const TvmType *st = &vm->types[f->type];
      put_u32(&sv.statics, save_type(&sv, f->type));
      for (uint32_t k = 0; k < st->nleaves; k++)
        save_slot(&sv, &sv.statics, st->leaves[k].kind,
                  vm->statics[f->slot + k]);
    }
  }
  for (uint32_t i = 0; i < sv.nobjs && !sv.error; i++)
    save_object(&sv, sv.objs[i]);
  for (uint32_t i = 0; i < sv.ntypes && !sv.error; i++)
    save_type_shape(&sv, i);
  Buf out = {0};
  if (!sv.error) {
    put_u32(&out, STATE_MAGIC);
    put_u32(&out, STATE_VERSION);
    put_u32(&out, sv.ntypes);
    put(&out, sv.types.p, sv.types.n);
    put_u32(&out, sv.nobjs);
    put(&out, sv.heads.p, sv.heads.n);
    put(&out, sv.bodies.p, sv.bodies.n);
    put(&out, sv.statics.p, sv.statics.n);
    if (out.failed || sv.types.failed || sv.heads.failed || sv.bodies.failed ||
        sv.statics.failed)
      sv.error = "out of memory";
  }
  free(sv.types.p);
  free(sv.heads.p);
  free(sv.bodies.p);
  free(sv.statics.p);
  free(sv.type_at);
  free(sv.type_of);
  free(sv.objs);
  free(sv.seen_keys);
  free(sv.seen_vals);
  if (sv.error) {
    free(out.p);
    tvm_set_error(vm, "tvm_state_save: %s", sv.error);
    return TVM_LOAD_ERROR;
  }
  *bytes = out.p;
  *size = out.n;
  return TVM_OK;
}

void tvm_state_free(uint8_t *bytes) { free(bytes); }

/* ---- 読む ---- */

typedef struct StaticValue {
  uint32_t slot;
  TvmSlot value;
} StaticValue;

typedef struct Load {
  TvmVm *vm;
  TcsReader r;
  uint32_t ntypes;
  uint16_t *types; /* 状態の型の番号 → VM の型 */
  /* struct の型の対応を後で確かめる組 (状態の型の番号、VM の型) */
  uint32_t *check_state;
  uint16_t *check_vm;
  uint32_t nchecks, capchecks;
  uint32_t nobjs;
  void **objs;
  uint32_t *counts;
  StaticValue *statics;
  uint32_t nstatics, capstatics;
  bool failed;
} Load;

static bool lfail(Load *ld, const char *fmt, const char *what) {
  if (!ld->failed) {
    char msg[512];
    snprintf(msg, sizeof msg, fmt, what);
    tvm_set_error(ld->vm, "tvm_state_load: %s", msg);
    ld->failed = true;
  }
  return false;
}

static float rd_f32(TcsReader *r) {
  uint32_t bits = tcs_rd_u32(r);
  float f;
  memcpy(&f, &bits, 4);
  return f;
}

static uint64_t rd_u64(TcsReader *r) {
  uint64_t lo = tcs_rd_u32(r);
  return lo | ((uint64_t)tcs_rd_u32(r) << 32);
}

/* 名前を buf に読む (終端の 0 を付ける) */
static bool rd_name(Load *ld, char *buf) {
  uint32_t n = tcs_rd_u32(&ld->r);
  if (ld->r.bad || n >= NAME_MAX_LEN || (size_t)(ld->r.end - ld->r.p) < n)
    return lfail(ld, "%s", "truncated state");
  memcpy(buf, ld->r.p, n);
  buf[n] = '\0';
  ld->r.p += n;
  return true;
}

static bool load_check(Load *ld, uint32_t state_type, uint16_t vm_type) {
  if (ld->nchecks == ld->capchecks) {
    uint32_t cap = ld->capchecks ? ld->capchecks * 2 : 64;
    uint32_t *s = (uint32_t *)realloc(ld->check_state, cap * sizeof(uint32_t));
    if (s)
      ld->check_state = s;
    uint16_t *v = (uint16_t *)realloc(ld->check_vm, cap * sizeof(uint16_t));
    if (v)
      ld->check_vm = v;
    if (!s || !v)
      return lfail(ld, "%s", "out of memory");
    ld->capchecks = cap;
  }
  ld->check_state[ld->nchecks] = state_type;
  ld->check_vm[ld->nchecks++] = vm_type;
  return true;
}

/* 要素の種類と struct の型が今の型と同じか */
static bool load_elem_shape(Load *ld, const TvmType *t) {
  uint8_t kind = tcs_rd_u8(&ld->r);
  uint32_t st = tcs_rd_u32(&ld->r);
  if (kind != t->elem_kind)
    return lfail(ld, "type %s has a different element", t->name);
  if (kind == TVM_K_STRUCT)
    return load_check(ld, st, t->elem_type);
  return true;
}

static bool load_types(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN], field[NAME_MAX_LEN];
  ld->ntypes = tcs_rd_u32(&ld->r);
  if (!tcs_rd_count_ok(&ld->r, ld->ntypes, 5))
    return lfail(ld, "%s", "truncated state");
  ld->types = (uint16_t *)malloc((ld->ntypes ? ld->ntypes : 1) * 2);
  if (!ld->types)
    return lfail(ld, "%s", "out of memory");
  for (uint32_t i = 0; i < ld->ntypes; i++) {
    if (!rd_name(ld, name))
      return false;
    uint8_t tkind = tcs_rd_u8(&ld->r);
    const TvmType *t = tvm_type(vm, name);
    if (!t)
      return lfail(ld, "this version has no type %s", name);
    if (t->tkind != tkind)
      return lfail(ld, "type %s is a different kind of type", name);
    ld->types[i] = (uint16_t)(t - vm->types);
    switch (tkind) {
    case TVM_T_CLASS:
    case TVM_T_STRUCT: {
      uint8_t host = tcs_rd_u8(&ld->r);
      uint32_t nfields = tcs_rd_u32(&ld->r);
      if (ld->r.bad)
        return lfail(ld, "%s", "truncated state");
      if (nfields != t->nfields ||
          host != (t->flags & TCS_TYPE_HOST_VALUE ? 1 : 0))
        return lfail(ld, "type %s has different fields", name);
      for (uint32_t k = 0; k < nfields; k++) {
        if (!rd_name(ld, field))
          return false;
        uint8_t kind = tcs_rd_u8(&ld->r);
        uint32_t st = tcs_rd_u32(&ld->r);
        if (strcmp(field, t->fields[k].name) != 0 || kind != t->fields[k].kind)
          return lfail(ld, "type %s has different fields", name);
        if (kind == TVM_K_STRUCT && !load_check(ld, st, t->fields[k].type))
          return false;
      }
      break;
    }
    case TVM_T_DICT:
      if (tcs_rd_u8(&ld->r) != t->key_kind)
        return lfail(ld, "type %s has a different key", name);
      if (!load_elem_shape(ld, t))
        return false;
      break;
    case TVM_T_ARRAY:
    case TVM_T_LIST:
    case TVM_T_BOX:
      if (!load_elem_shape(ld, t))
        return false;
      break;
    case TVM_T_STRING:
    case TVM_T_FUNC:
      break;
    default:
      return lfail(ld, "type %s cannot be loaded", name);
    }
    if (ld->r.bad)
      return lfail(ld, "%s", "truncated state");
  }
  for (uint32_t i = 0; i < ld->nchecks; i++)
    if (ld->check_state[i] >= ld->ntypes ||
        ld->types[ld->check_state[i]] != ld->check_vm[i])
      return lfail(ld, "type %s has a different struct inside",
                   vm->types[ld->check_vm[i]].name);
  return true;
}

/* 参照を読む。置き場所の型 (TVM_NO_TYPE は任意) に合わない object は断る */
static bool load_ref(Load *ld, uint16_t type, void **out) {
  uint32_t at = tcs_rd_u32(&ld->r);
  *out = NULL;
  if (ld->r.bad)
    return lfail(ld, "%s", "truncated state");
  if (!at)
    return true;
  if (at > ld->nobjs)
    return lfail(ld, "%s", "a reference is out of range");
  void *p = ld->objs[at - 1];
  if (type != TVM_NO_TYPE) {
    if (type >= ld->vm->ntypes ||
        !tvm_is_type(ld->vm, (const TvmObject *)p, &ld->vm->types[type]))
      return lfail(ld, "a reference to %s does not fit its place",
                   TVM_TYPE(p)->name);
  }
  *out = p;
  return true;
}

static bool load_slot(Load *ld, uint8_t kind, uint16_t type, TvmSlot *s) {
  s->u = 0;
  switch (kind) {
  case TVM_K_I32:
    s->i = (int32_t)tcs_rd_u32(&ld->r);
    break;
  case TVM_K_F32:
    s->f = rd_f32(&ld->r);
    break;
  case TVM_K_BOOL:
    s->i = tcs_rd_u8(&ld->r) ? 1 : 0;
    break;
  case TVM_K_REF:
    return load_ref(ld, type, &s->p);
  default:
    return lfail(ld, "%s", "a value has a kind that cannot be loaded");
  }
  return !ld->r.bad || lfail(ld, "%s", "truncated state");
}

static bool load_value(Load *ld, uint8_t kind, uint16_t type, char *mem) {
  switch (kind) {
  case TVM_K_I32:
  case TVM_K_F32: {
    uint32_t v = tcs_rd_u32(&ld->r);
    memcpy(mem, &v, 4);
    break;
  }
  case TVM_K_BOOL:
    *(uint8_t *)mem = tcs_rd_u8(&ld->r) ? 1 : 0;
    break;
  case TVM_K_REF: {
    void *p;
    if (!load_ref(ld, type, &p))
      return false;
    memcpy(mem, &p, sizeof p);
    break;
  }
  default: {
    const TvmType *st = &ld->vm->types[type];
    for (uint32_t k = 0; k < st->nfields; k++)
      if (!load_value(ld, st->fields[k].kind, st->fields[k].type,
                      mem + st->fields[k].offset))
        return false;
    break;
  }
  }
  return !ld->r.bad || lfail(ld, "%s", "truncated state");
}

/* object を作る。中身は後で入れる (string だけはここで入れる。Dictionary の key の hash が読む) */
static bool load_heads(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN];
  ld->nobjs = tcs_rd_u32(&ld->r);
  if (!tcs_rd_count_ok(&ld->r, ld->nobjs, 8))
    return lfail(ld, "%s", "truncated state");
  uint32_t n = ld->nobjs ? ld->nobjs : 1;
  ld->objs = (void **)calloc(n, sizeof(void *));
  ld->counts = (uint32_t *)calloc(n, sizeof(uint32_t));
  if (!ld->objs || !ld->counts)
    return lfail(ld, "%s", "out of memory");
  for (uint32_t i = 0; i < ld->nobjs; i++) {
    uint32_t ti = tcs_rd_u32(&ld->r);
    uint32_t count = tcs_rd_u32(&ld->r);
    if (ld->r.bad || ti >= ld->ntypes)
      return lfail(ld, "%s", "truncated state");
    const TvmType *t = &vm->types[ld->types[ti]];
    void *p = NULL;
    ld->counts[i] = count;
    switch (t->tkind) {
    case TVM_T_CLASS:
      p = tvm_object_new_of(vm, t);
      break;
    case TVM_T_ARRAY:
      if (count > 0x7FFFFFFF)
        return lfail(ld, "%s", "an array is too long");
      p = tvm_array_new_of(vm, t, (int32_t)count);
      break;
    case TVM_T_STRING:
      if ((size_t)(ld->r.end - ld->r.p) < count)
        return lfail(ld, "%s", "truncated state");
      p = tvm_str_new(vm, (const char *)ld->r.p, count);
      ld->r.p += count;
      break;
    case TVM_T_LIST:
      if (count > 0x7FFFFFFF)
        return lfail(ld, "%s", "a list is too long");
      p = tvm_list_new_of(vm, t, (int32_t)count);
      break;
    case TVM_T_DICT:
      p = tvm_dict_new(vm, t);
      break;
    case TVM_T_FUNC: {
      if (!rd_name(ld, name))
        return false;
      int32_t f = tvm_find(vm, name);
      if (f < 0)
        return lfail(ld, "this version has no function %s", name);
      const TvmProto *g = &vm->funcs[f];
      if (g->ncaps != count || !tvm_proto_matches(g, t))
        return lfail(ld, "function %s has a different shape", name);
      TvmClosure *c = (TvmClosure *)tvm_alloc(
          vm, sizeof(TvmClosure) + sizeof(TvmSlot) * g->ncaps);
      if (c) {
        TVM_TYPE(c) = t;
        c->proto = g;
        c->ncaps = g->ncaps;
      }
      p = c;
      break;
    }
    case TVM_T_BOX: {
      TvmBox *bx = (TvmBox *)tvm_alloc(vm, sizeof(TvmBox));
      if (bx)
        TVM_TYPE(bx) = t;
      p = bx;
      break;
    }
    default:
      return lfail(ld, "type %s cannot be an object", t->name);
    }
    if (!p)
      return lfail(ld, "%s", "out of memory");
    ld->objs[i] = p;
  }
  return true;
}

static bool load_bodies(Load *ld) {
  TvmVm *vm = ld->vm;
  for (uint32_t i = 0; i < ld->nobjs; i++) {
    void *p = ld->objs[i];
    const TvmType *t = TVM_TYPE(p);
    uint32_t count = ld->counts[i];
    switch (t->tkind) {
    case TVM_T_CLASS:
      for (uint32_t k = 0; k < t->nfields; k++)
        if (!load_value(ld, t->fields[k].kind, t->fields[k].type,
                        (char *)p + t->fields[k].offset))
          return false;
      if (t->flags & TCS_TYPE_HOST_VALUE)
        TVM_HOST_VALUE(p) = rd_u64(&ld->r);
      break;
    case TVM_T_ARRAY:
      for (uint32_t k = 0; k < count; k++)
        if (!load_value(ld, t->elem_kind, t->elem_type,
                        ARR_DATA((TvmArray *)p) + (size_t)k * t->elem_size))
          return false;
      break;
    case TVM_T_LIST:
      for (uint32_t k = 0; k < count; k++)
        if (!load_value(ld, t->elem_kind, t->elem_type,
                        ((TvmList *)p)->data + (size_t)k * t->elem_size))
          return false;
      break;
    case TVM_T_DICT: {
      TvmDict *d = (TvmDict *)p;
      for (uint32_t k = 0; k < count; k++) {
        TvmSlot key;
        if (!load_slot(ld, t->key_kind, t->key_type, &key))
          return false;
        int32_t at = tvm_dict_insert(vm, d, key);
        if (at < 0)
          return lfail(ld, "%s", "out of memory");
        if (!load_value(ld, t->elem_kind, t->elem_type,
                        DICT_VALUE(DICT_ENTRY(d, at))))
          return false;
      }
      break;
    }
    case TVM_T_FUNC: {
      TvmClosure *c = (TvmClosure *)p;
      for (uint32_t k = 0; k < c->ncaps; k++)
        if (!load_slot(ld, c->proto->caps[k].kind, c->proto->caps[k].type,
                       &CLO_CAPS(c)[k]))
          return false;
      break;
    }
    case TVM_T_BOX:
      if (!load_slot(ld, t->elem_kind, TVM_NO_TYPE, &((TvmBox *)p)->value))
        return false;
      break;
    default:
      break;
    }
    if (ld->r.bad)
      return lfail(ld, "%s", "truncated state");
  }
  return true;
}

static bool load_static(Load *ld, uint32_t slot, uint8_t kind, uint16_t type,
                        bool keep) {
  TvmSlot v;
  if (!load_slot(ld, kind, type, &v))
    return false;
  if (!keep)
    return true;
  if (ld->nstatics == ld->capstatics) {
    uint32_t cap = ld->capstatics ? ld->capstatics * 2 : 256;
    StaticValue *s =
        (StaticValue *)realloc(ld->statics, cap * sizeof(StaticValue));
    if (!s)
      return lfail(ld, "%s", "out of memory");
    ld->statics = s;
    ld->capstatics = cap;
  }
  ld->statics[ld->nstatics].slot = slot;
  ld->statics[ld->nstatics++].value = v;
  return true;
}

/* static を名前で対応させて読む。今の module に無い static の値は読み捨てる */
static bool load_statics(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN];
  uint32_t n = tcs_rd_u32(&ld->r);
  if (!tcs_rd_count_ok(&ld->r, n, 9))
    return lfail(ld, "%s", "truncated state");
  for (uint32_t i = 0; i < n; i++) {
    if (!rd_name(ld, name))
      return false;
    uint8_t kind = tcs_rd_u8(&ld->r);
    uint32_t st = tcs_rd_u32(&ld->r);
    if (ld->r.bad || kind > TVM_K_STRUCT ||
        (kind == TVM_K_STRUCT && st >= ld->ntypes))
      return lfail(ld, "%s", "truncated state");
    const TvmStaticField *f = NULL;
    for (uint32_t k = 0; k < vm->nstatic_fields && !f; k++)
      if (strcmp(vm->static_fields[k].name, name) == 0)
        f = &vm->static_fields[k];
    if (f &&
        (f->kind != kind || (kind == TVM_K_STRUCT && ld->types[st] != f->type)))
      return lfail(ld, "static %s has a different type", name);
    if (kind != TVM_K_STRUCT) {
      /* 読み捨てる参照は置き場所の型を持たない */
      if (!load_static(ld, f ? f->slot : 0, kind, f ? f->type : TVM_NO_TYPE,
                       f != NULL))
        return false;
      continue;
    }
    const TvmType *t = &vm->types[ld->types[st]];
    if (t->tkind != TVM_T_STRUCT)
      return lfail(ld, "static %s has a different type", name);
    for (uint32_t k = 0; k < t->nleaves; k++)
      if (!load_static(ld, f ? f->slot + k : 0, t->leaves[k].kind,
                       t->leaves[k].type, f != NULL))
        return false;
  }
  return true;
}

TvmStatus tvm_state_load(TvmVm *vm, const uint8_t *bytes, size_t size) {
  vm->error[0] = '\0';
  if (!at_frame_boundary(vm, "tvm_state_load"))
    return TVM_LOAD_ERROR;
  Load ld = {0};
  ld.vm = vm;
  ld.r.p = bytes;
  ld.r.end = bytes + size;
  bool ok = true;
  if (tcs_rd_u32(&ld.r) != STATE_MAGIC || tcs_rd_u32(&ld.r) != STATE_VERSION ||
      ld.r.bad)
    ok = lfail(&ld, "%s", "not a state of this format");
  ok = ok && load_types(&ld) && load_heads(&ld) && load_bodies(&ld) &&
       load_statics(&ld);
  if (ok && ld.r.p != ld.r.end)
    ok = lfail(&ld, "%s", "the state has extra bytes");
  if (ok) {
    /* ここからは失敗しない。作った object は static から届き、前の object は次の回収で消える */
    for (uint32_t i = 0; i < ld.nstatics; i++)
      vm->statics[ld.statics[i].slot] = ld.statics[i].value;
  }
  free(ld.types);
  free(ld.check_state);
  free(ld.check_vm);
  free(ld.objs);
  free(ld.counts);
  free(ld.statics);
  return ok ? TVM_OK : TVM_LOAD_ERROR;
}
