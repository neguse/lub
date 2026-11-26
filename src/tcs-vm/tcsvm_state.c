/* Saving and loading state (docs/tcs/vm-design.md §13.4).
   Writes statics and the objects reachable from them into a byte array that
   does not depend on addresses or layout. The reader is another VM (even in
   another process) with types of the same shape; it recreates the objects and
   stores them into statics.

   Byte array (little endian):
     u32 magic, u32 version
     Type table: u32 count, then per type: name, u8 kind, shape
       class / struct: u8 whether it has a host value, u32 field count, then per
   field: name, u8 kind, u32 struct type (struct fields only), declared type
   name (reference fields only) array / List / box: u8 element kind, u32 element
   struct type Dictionary: u8 key kind, u8 value kind, u32 value struct type
     Object headers: u32 count, then per object: u32 type, u32 count (elements
   for arrays, List, and Dictionary, bytes for string, captures for closure),
       the bytes for string, the function name for closure
     Object contents: in the same order as the headers
     statics: u32 count, then per static: name, u8 kind, u32 struct type,
   declared type name (reference statics only), value Inspection serial numbers:
   u32 count, then per pair: u32 object index, u64 serial number (only objects
   that inspection assigned a number to) Values: int and float are 4 bytes, bool
   is 1 byte, references are u32 (object index + 1; 0 is null), structs are in
   field order. Names are a u32 length and bytes.

   Loading matches types by name to the types of the current module. If the
   shape of a class or struct differs, it is migrated by the same rules as the
   reload plan (docs/tcs/object-model.md §6): fields are matched by name; if the
   kind and type name are the same they get the state value, otherwise the zero
   value; fields not in the state get the zero value and then their initializer;
   fields not in the current type are skipped. Checks that reference targets fit
   the type of their location and that closure functions fit their function
   type. Statics are not touched until the checks finish, so on rejection the
   state is unchanged. Objects held by host handles are not written (after
   loading, the handles still point to the previous objects). */
#include "tcsvm_internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define STATE_MAGIC 0x53534354u /* "TCSS" */
#define STATE_VERSION 3u
#define NONE 0xFFFFFFFFu
#define NAME_MAX_LEN 1024u

/* ---- Writing ---- */

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
  uint32_t *type_at; /* VM type → state type number (NONE means not yet) */
  uint16_t *type_of; /* State type number → VM type */
  uint32_t ntypes;
  void **objs; /* Index → object (in the order found; pushed at the back,
                  written from the front) */
  uint32_t nobjs, capobjs;
  void **seen_keys; /* object → index (open addressing) */
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

/* Reference value: object index + 1 (an object seen for the first time is
 * pushed at the back of the list) */
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

/* One value of a register or static */
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

/* A value inside an object (field, element) */
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

/* The declared type name of a reference location ("" for untyped locations) */
static const char *declared_name(const TvmVm *vm, uint16_t type) {
  return type < vm->ntypes ? vm->types[type].name : "";
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
      if (t->fields[k].kind == TVM_K_REF)
        put_str(b, declared_name(sv->vm, t->fields[k].type));
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
        if (f->kind == TVM_K_REF)
          put_str(&sv.statics, declared_name(vm, f->type));
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
    /* Carries the serial numbers assigned by inspection (so that the same
     * individual gets the same id where it is loaded) */
    uint32_t nids = 0;
    for (uint32_t i = 0; i < sv.nobjs; i++)
      if (tvm_debug_id_peek(vm, sv.objs[i]))
        nids++;
    put_u32(&out, nids);
    for (uint32_t i = 0; i < sv.nobjs; i++) {
      uint64_t id = tvm_debug_id_peek(vm, sv.objs[i]);
      if (!id)
        continue;
      put_u32(&out, i);
      put_u64(&out, id);
    }
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

/* ---- Reading ---- */

static void slot_to_mem(uint8_t kind, TvmSlot s, char *p) {
  switch (kind) {
  case TVM_K_I32:
    memcpy(p, &s.i, 4);
    break;
  case TVM_K_F32:
    memcpy(p, &s.f, 4);
    break;
  case TVM_K_BOOL:
    *(uint8_t *)p = (uint8_t)s.i;
    break;
  default:
    memcpy(p, &s.p, sizeof(void *));
    break;
  }
}

typedef struct StaticValue {
  uint32_t slot;
  TvmSlot value;
} StaticValue;

/* A field of a class / struct in the state. to is the field index in the
 * current type (-1 for fields whose values are not moved) */
typedef struct SavedField {
  uint8_t kind;
  uint32_t st; /* State type number of a struct field */
  int32_t to;
} SavedField;

/* Shape of a state type. Fields for class / struct, the element struct type for
 * containers */
typedef struct SavedType {
  SavedField *fields;
  uint32_t nfields;
  uint32_t
      elem_st; /* State type number of a container whose elements are structs */
  /* Fields of the current type whose values were not moved from the state
   * (class only; their initializers run after loading) */
  uint32_t *fresh;
  uint32_t nfresh;
} SavedType;

typedef struct Load {
  TvmVm *vm;
  TcsReader r;
  uint32_t ntypes;
  uint16_t *types;   /* State type number → VM type */
  SavedType *shapes; /* State type number → state shape */
  /* Pairs (state type number, VM type) for checking the element struct type
   * mapping of containers later */
  uint32_t *check_state;
  uint16_t *check_vm;
  uint32_t nchecks, capchecks;
  uint32_t nobjs;
  void **objs;
  uint32_t *obj_types; /* State type number of each object */
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

/* Reads a name into buf (with a terminating 0) */
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

/* Whether the element kind and struct type are the same as in the current type
 */
static bool load_elem_shape(Load *ld, uint32_t i, const TvmType *t) {
  uint8_t kind = tcs_rd_u8(&ld->r);
  uint32_t st = tcs_rd_u32(&ld->r);
  if (kind != t->elem_kind)
    return lfail(ld, "type %s has a different element", t->name);
  ld->shapes[i].elem_st = st;
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
  ld->shapes =
      (SavedType *)calloc(ld->ntypes ? ld->ntypes : 1, sizeof(SavedType));
  if (!ld->types || !ld->shapes)
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
      if (host != (t->flags & TCS_TYPE_HOST_VALUE ? 1 : 0))
        return lfail(ld, "type %s has a different host value", name);
      if (!tcs_rd_count_ok(&ld->r, nfields, 9))
        return lfail(ld, "%s", "truncated state");
      SavedType *shape = &ld->shapes[i];
      shape->fields =
          (SavedField *)calloc(nfields ? nfields : 1, sizeof(SavedField));
      if (!shape->fields)
        return lfail(ld, "%s", "out of memory");
      shape->nfields = nfields;
      for (uint32_t k = 0; k < nfields; k++) {
        if (!rd_name(ld, field))
          return false;
        SavedField *sf = &shape->fields[k];
        sf->kind = tcs_rd_u8(&ld->r);
        sf->st = tcs_rd_u32(&ld->r);
        sf->to = -1;
        if (ld->r.bad || sf->kind > TVM_K_STRUCT)
          return lfail(ld, "%s", "truncated state");
        /* Matches by name, and moves the value if the kind and type name are
         * the same (struct types are checked after all types are read) */
        for (uint32_t j = 0; j < t->nfields && sf->to < 0; j++)
          if (strcmp(field, t->fields[j].name) == 0 &&
              t->fields[j].kind == sf->kind)
            sf->to = (int32_t)j;
        if (sf->kind == TVM_K_REF) {
          if (!rd_name(ld, field))
            return false;
          if (sf->to >= 0 &&
              strcmp(field, declared_name(vm, t->fields[sf->to].type)) != 0)
            sf->to = -1;
        }
      }
      break;
    }
    case TVM_T_DICT:
      if (tcs_rd_u8(&ld->r) != t->key_kind)
        return lfail(ld, "type %s has a different key", name);
      if (!load_elem_shape(ld, i, t))
        return false;
      break;
    case TVM_T_ARRAY:
    case TVM_T_LIST:
    case TVM_T_BOX:
      if (!load_elem_shape(ld, i, t))
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
  /* A struct field's value is moved only when the state struct type maps to the
     type of the current field. Fields of the current class whose values are not
     moved are recorded as fields whose initializers run */
  for (uint32_t i = 0; i < ld->ntypes; i++) {
    SavedType *shape = &ld->shapes[i];
    const TvmType *t = &vm->types[ld->types[i]];
    if (t->tkind != TVM_T_CLASS && t->tkind != TVM_T_STRUCT)
      continue;
    for (uint32_t k = 0; k < shape->nfields; k++) {
      SavedField *sf = &shape->fields[k];
      if (sf->kind != TVM_K_STRUCT)
        continue;
      if (sf->st >= ld->ntypes ||
          vm->types[ld->types[sf->st]].tkind != TVM_T_STRUCT)
        return lfail(ld, "type %s has a bad struct field", t->name);
      if (sf->to >= 0 && ld->types[sf->st] != t->fields[sf->to].type)
        sf->to = -1;
    }
    if (t->tkind != TVM_T_CLASS || !t->nfields)
      continue;
    shape->fresh = (uint32_t *)malloc(t->nfields * sizeof(uint32_t));
    if (!shape->fresh)
      return lfail(ld, "%s", "out of memory");
    for (uint32_t j = 0; j < t->nfields; j++) {
      bool moved = false;
      for (uint32_t k = 0; k < shape->nfields && !moved; k++)
        moved = shape->fields[k].to == (int32_t)j;
      if (!moved)
        shape->fresh[shape->nfresh++] = j;
    }
  }
  return true;
}

/* Reads a reference. Objects that do not fit the type of the location
 * (TVM_NO_TYPE means any) are rejected */
static bool load_ref(Load *ld, uint16_t type, void **out) {
  uint32_t at = tcs_rd_u32(&ld->r);
  void *unused;
  if (!out)
    out = &unused;
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

static bool load_fields(Load *ld, uint32_t st, char *mem);

/* Reads a value inside an object (field, element). kind is the state kind, st
   is the state type number of a struct value, type is the type of the reference
   location. If mem is NULL, the value is skipped */
static bool load_value(Load *ld, uint8_t kind, uint32_t st, uint16_t type,
                       char *mem) {
  switch (kind) {
  case TVM_K_I32:
  case TVM_K_F32: {
    uint32_t v = tcs_rd_u32(&ld->r);
    if (mem)
      memcpy(mem, &v, 4);
    break;
  }
  case TVM_K_BOOL: {
    uint8_t v = tcs_rd_u8(&ld->r) ? 1 : 0;
    if (mem)
      *(uint8_t *)mem = v;
    break;
  }
  case TVM_K_REF: {
    void *p;
    if (!load_ref(ld, mem ? type : TVM_NO_TYPE, &p))
      return false;
    if (mem)
      memcpy(mem, &p, sizeof p);
    break;
  }
  case TVM_K_STRUCT:
    return load_fields(ld, st, mem);
  default:
    return lfail(ld, "%s", "a value has a kind that cannot be loaded");
  }
  return !ld->r.bad || lfail(ld, "%s", "truncated state");
}

/* Reads the fields of the class / struct of state type st in state order and
   moves them to the fields of the current type (fields not moved are skipped).
   mem is the start of the value of the current type (if NULL, everything is
   skipped) */
static bool load_fields(Load *ld, uint32_t st, char *mem) {
  if (st >= ld->ntypes)
    return lfail(ld, "%s", "a value has a bad type");
  const SavedType *shape = &ld->shapes[st];
  const TvmType *t = &ld->vm->types[ld->types[st]];
  if (t->tkind != TVM_T_CLASS && t->tkind != TVM_T_STRUCT)
    return lfail(ld, "%s", "a value has a bad type");
  for (uint32_t k = 0; k < shape->nfields; k++) {
    const SavedField *sf = &shape->fields[k];
    bool keep = mem && sf->to >= 0;
    if (!load_value(ld, sf->kind, sf->st,
                    keep ? t->fields[sf->to].type : TVM_NO_TYPE,
                    keep ? mem + t->fields[sf->to].offset : NULL))
      return false;
  }
  return true;
}

/* Creates an object. Contents are filled in later (only strings are filled in
 * here, because Dictionary key hashing reads them) */
static bool load_heads(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN];
  ld->nobjs = tcs_rd_u32(&ld->r);
  if (!tcs_rd_count_ok(&ld->r, ld->nobjs, 8))
    return lfail(ld, "%s", "truncated state");
  uint32_t n = ld->nobjs ? ld->nobjs : 1;
  ld->objs = (void **)calloc(n, sizeof(void *));
  ld->obj_types = (uint32_t *)calloc(n, sizeof(uint32_t));
  ld->counts = (uint32_t *)calloc(n, sizeof(uint32_t));
  if (!ld->objs || !ld->obj_types || !ld->counts)
    return lfail(ld, "%s", "out of memory");
  for (uint32_t i = 0; i < ld->nobjs; i++) {
    uint32_t ti = tcs_rd_u32(&ld->r);
    uint32_t count = tcs_rd_u32(&ld->r);
    if (ld->r.bad || ti >= ld->ntypes)
      return lfail(ld, "%s", "truncated state");
    const TvmType *t = &vm->types[ld->types[ti]];
    void *p = NULL;
    ld->obj_types[i] = ti;
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
    uint32_t est = ld->shapes[ld->obj_types[i]].elem_st;
    switch (t->tkind) {
    case TVM_T_CLASS:
      if (!load_fields(ld, ld->obj_types[i], (char *)p))
        return false;
      if (t->flags & TCS_TYPE_HOST_VALUE)
        TVM_HOST_VALUE(p) = rd_u64(&ld->r);
      break;
    case TVM_T_ARRAY:
      for (uint32_t k = 0; k < count; k++)
        if (!load_value(ld, t->elem_kind, est, t->elem_type,
                        ARR_DATA((TvmArray *)p) + (size_t)k * t->elem_size))
          return false;
      break;
    case TVM_T_LIST:
      for (uint32_t k = 0; k < count; k++)
        if (!load_value(ld, t->elem_kind, est, t->elem_type,
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
        if (!load_value(ld, t->elem_kind, est, t->elem_type,
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

/* Reads the leaves of a struct static in the field order of state type st.
   root is the current struct type of the static, base is the offset within root
   of the struct value being read, slot is the first slot of the static. If keep
   is false, they are skipped */
static bool load_static_struct(Load *ld, uint32_t st, const TvmType *root,
                               uint32_t base, uint32_t slot, bool keep) {
  if (st >= ld->ntypes)
    return lfail(ld, "%s", "a static has a bad type");
  const SavedType *shape = &ld->shapes[st];
  const TvmType *t = &ld->vm->types[ld->types[st]];
  if (t->tkind != TVM_T_STRUCT)
    return lfail(ld, "%s", "a static has a bad type");
  for (uint32_t k = 0; k < shape->nfields; k++) {
    const SavedField *sf = &shape->fields[k];
    bool moved = keep && sf->to >= 0;
    uint32_t offset = moved ? base + t->fields[sf->to].offset : 0;
    if (sf->kind == TVM_K_STRUCT) {
      if (!load_static_struct(ld, sf->st, root, offset, slot, moved))
        return false;
      continue;
    }
    /* The leaf of the current field (the leaf at the same offset in root) */
    uint32_t leaf = root->nleaves;
    for (uint32_t l = 0; moved && l < root->nleaves && leaf == root->nleaves;
         l++)
      if (root->leaves[l].offset == offset && root->leaves[l].kind == sf->kind)
        leaf = l;
    moved = moved && leaf < root->nleaves;
    if (!load_static(ld, moved ? slot + leaf : 0, sf->kind,
                     moved ? root->leaves[leaf].type : TVM_NO_TYPE, moved))
      return false;
  }
  return true;
}

/* Reads statics matched by name. Values of statics not in the current module
 * and of statics whose kind or type changed are skipped (they keep their
 * current values) */
static bool load_statics(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN], declared[NAME_MAX_LEN];
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
    bool keep = f && f->kind == kind;
    if (kind == TVM_K_REF) {
      if (!rd_name(ld, declared))
        return false;
      keep = keep && strcmp(declared, declared_name(vm, f->type)) == 0;
    }
    if (kind != TVM_K_STRUCT) {
      /* References being skipped have no location type */
      if (!load_static(ld, keep ? f->slot : 0, kind,
                       keep ? f->type : TVM_NO_TYPE, keep))
        return false;
      continue;
    }
    keep = keep && ld->types[st] == f->type;
    if (!load_static_struct(ld, st, keep ? &vm->types[f->type] : NULL, 0,
                            keep ? f->slot : 0, keep))
      return false;
  }
  return true;
}

/* Puts initializer values into class fields whose values were not moved from
   the state (added fields and fields whose kind or type changed). The
   initializer is the function `Type.<init>field` (it takes no arguments and
   returns the field value). Fields without one keep the zero value. A fault
   becomes a warning (kept in vm->error), and that field keeps the zero value */
static void run_initializers(Load *ld) {
  TvmVm *vm = ld->vm;
  char name[NAME_MAX_LEN * 2 + 16];
  char warnings[sizeof vm->error];
  warnings[0] = '\0';
  /* Pushes objects onto the root so that objects not reachable from statics
   * (those pointed to only by skipped fields) do not disappear even if an
   * initializer's allocation triggers a collection */
  size_t need = 0;
  for (uint32_t i = 0; i < ld->nobjs; i++)
    need += ld->shapes[ld->obj_types[i]].nfresh ? 1 : 0;
  if (!need)
    return;
  if (vm->capreload_roots < need) {
    void **roots = (void **)realloc(vm->reload_roots, need * sizeof(void *));
    if (!roots)
      return; /* Does not run the initializer (the field keeps the zero value)
               */
    vm->reload_roots = roots;
    vm->capreload_roots = need;
  }
  vm->nreload_roots = 0;
  for (uint32_t i = 0; i < ld->nobjs; i++)
    if (ld->shapes[ld->obj_types[i]].nfresh)
      vm->reload_roots[vm->nreload_roots++] = ld->objs[i];
  for (uint32_t i = 0; i < ld->nobjs; i++) {
    const SavedType *shape = &ld->shapes[ld->obj_types[i]];
    char *o = (char *)ld->objs[i];
    if (!shape->nfresh)
      continue;
    const TvmType *t = TVM_TYPE(o);
    for (uint32_t k = 0; k < shape->nfresh; k++) {
      const TcsField *field = &t->fields[shape->fresh[k]];
      snprintf(name, sizeof name, "%s.<init>%s", t->name, field->name);
      int32_t func = tvm_find(vm, name);
      if (func < 0)
        continue;
      const TvmProto *f = &vm->funcs[func];
      TvmSlot rets[256];
      if (f->nparams || f->nret > 256)
        continue;
      memset(rets, 0, sizeof rets);
      if (tvm_call(vm, func, NULL, 0, rets, f->nret) != TVM_OK) {
        size_t used = strlen(warnings);
        snprintf(warnings + used, sizeof warnings - used, "%s%s",
                 used ? "\n" : "", vm->error);
        continue;
      }
      if (field->kind == TVM_K_STRUCT) {
        const TvmType *st = &vm->types[field->type];
        for (uint32_t j = 0; j < st->nleaves && j < f->nret; j++)
          slot_to_mem(st->leaves[j].kind, rets[j],
                      o + field->offset + st->leaves[j].offset);
      } else if (f->nret == 1)
        slot_to_mem(field->kind, rets[0], o + field->offset);
    }
  }
  vm->nreload_roots = 0;
  memcpy(vm->error, warnings, sizeof warnings);
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
  /* Inspection serial numbers are attached after the checks finish (ids is the
   * start of the pairs in the byte array) */
  const uint8_t *ids = NULL;
  uint32_t nids = 0;
  if (ok) {
    nids = tcs_rd_u32(&ld.r);
    ids = ld.r.p;
    for (uint32_t i = 0; i < nids && ok; i++) {
      uint32_t at = tcs_rd_u32(&ld.r);
      rd_u64(&ld.r);
      if (ld.r.bad || at >= ld.nobjs)
        ok = lfail(&ld, "%s", "a bad object id");
    }
  }
  if (ok && ld.r.p != ld.r.end)
    ok = lfail(&ld, "%s", "the state has extra bytes");
  if (ok) {
    /* Nothing fails from here on. The created objects are reachable from
     * statics, and the previous objects disappear at the next collection */
    for (uint32_t i = 0; i < ld.nstatics; i++)
      vm->statics[ld.statics[i].slot] = ld.statics[i].value;
    ld.r.p = ids;
    for (uint32_t i = 0; i < nids; i++) {
      uint32_t at = tcs_rd_u32(&ld.r);
      uint64_t id = rd_u64(&ld.r);
      /* Weakly pinned objects (tvm_inspect_pin) are moved to the recreated
       * object with the same number */
      for (uint32_t k = 0; k < vm->npinned; k++)
        if (vm->pinned[k].p && tvm_debug_id_peek(vm, vm->pinned[k].p) == id)
          vm->pinned[k].p = ld.objs[at];
      tvm_debug_id_set(vm, ld.objs[at], id);
    }
    run_initializers(&ld);
  }
  for (uint32_t i = 0; ld.shapes && i < ld.ntypes; i++) {
    free(ld.shapes[i].fields);
    free(ld.shapes[i].fresh);
  }
  free(ld.shapes);
  free(ld.obj_types);
  free(ld.types);
  free(ld.check_state);
  free(ld.check_vm);
  free(ld.objs);
  free(ld.counts);
  free(ld.statics);
  return ok ? TVM_OK : TVM_LOAD_ERROR;
}
