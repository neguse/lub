/* tcs VM の読み込み: parse、layout (doc/vm-design.md §9.3)、検査と link (§9.4)。 */
#include "tcsvm_internal.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

/* ---- エラー ---- */

void tvm_set_error(TvmVm *vm, const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(vm->error, sizeof vm->error, fmt, ap);
  va_end(ap);
}

void tvm_append_error(TvmVm *vm, const char *fmt, ...) {
  size_t len = strlen(vm->error);
  if (len + 1 >= sizeof vm->error)
    return;
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(vm->error + len, sizeof vm->error - len, fmt, ap);
  va_end(ap);
}

const char *tvm_error(TvmVm *vm) { return vm->error; }

/* ---- 生成と解放 ---- */

TvmVm *tvm_new(const TvmConfig *config) {
  TvmVm *vm = (TvmVm *)calloc(1, sizeof *vm);
  if (!vm)
    return NULL;
  size_t slots = config && config->stack_slots ? config->stack_slots : ((size_t)1 << 20);
  size_t frames = config && config->max_frames ? config->max_frames : ((size_t)1 << 16);
  vm->stack = (TvmSlot *)calloc(slots, sizeof(TvmSlot));
  vm->frames = (TvmFrame *)calloc(frames, sizeof(TvmFrame));
  if (!vm->stack || !vm->frames) {
    tvm_free(vm);
    return NULL;
  }
  vm->stack_end = vm->stack + slots;
  vm->frames_end = vm->frames + frames;
  vm->top = vm->stack;
  vm->ftop = vm->frames;
  vm->entry = -1;
  vm->string_type = TVM_NO_TYPE;
  vm->gc_stress = config && config->gc_stress;
  vm->gc_limit = vm->gc_stress ? 0 : ((size_t)1 << 20);
  return vm;
}

void tvm_free(TvmVm *vm) {
  if (!vm)
    return;
  for (uint32_t i = 0; i < vm->nstrings; i++)
    free(vm->strings[i]);
  free(vm->strings);
  for (uint32_t i = 0; i < vm->nconsts; i++)
    if (vm->consts[i])
      tvm_free_fixed(vm->consts[i]);
  free(vm->consts);
  tcs_model_free(vm->types, vm->ntypes);
  free(vm->static_info);
  free(vm->statics);
  free(vm->static_fields);
  for (uint32_t i = 0; i < vm->nretired; i++)
    tvm_free_fixed(vm->retired[i]);
  free(vm->retired);
  free(vm->reload_roots);
  free(vm->handles);
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    free(vm->funcs[i].regs);
    free(vm->funcs[i].rets);
    free(vm->funcs[i].caps);
    free(vm->funcs[i].code);
    free(vm->funcs[i].lines);
  }
  free(vm->funcs);
  free(vm->selectors);
  free(vm->intrinsics);
  for (uint32_t i = 0; i < vm->nhosts; i++) {
    free((char *)vm->hosts[i].name);
    free((char *)vm->hosts[i].params);
    free((char *)vm->hosts[i].rets);
  }
  free(vm->hosts);
  for (uint32_t i = 0; i < vm->nhost_types; i++)
    free(vm->host_types[i].name);
  free(vm->host_types);
  free(vm->type_ids);
  free(vm->iface_checks);
  while (vm->snapshots)
    tvm_snapshot_free(vm, vm->snapshots);
  tvm_debug_free(vm->debug);
  free(vm->pinned);
  tvm_heap_free(vm);
  free(vm->stack);
  free(vm->frames);
  free(vm);
}

/* ---- parse ---- */

typedef TcsReader Reader;
#define rd_u8 tcs_rd_u8
#define rd_u16 tcs_rd_u16
#define rd_u32 tcs_rd_u32
#define count_ok tcs_rd_count_ok

static uint64_t rd_u64(Reader *r) {
  uint64_t lo = rd_u32(r);
  uint64_t hi = rd_u32(r);
  return lo | (hi << 32);
}

static uint32_t op_abi_hash(void) {
  static const char *const names[] = {
#define TVM_OP_NAME(name) #name,
      TVM_OPS(TVM_OP_NAME)
#undef TVM_OP_NAME
  };
  uint32_t h = 2166136261u;
  for (size_t i = 0; i < sizeof names / sizeof names[0]; i++) {
    for (const char *s = names[i]; *s; s++)
      h = (h ^ (uint8_t)*s) * 16777619u;
    h = (h ^ (uint8_t)',') * 16777619u;
  }
  return h;
}

static const char *str_at(TvmVm *vm, uint32_t i) {
  return i < vm->nstrings ? vm->strings[i] : "?";
}

/* (kind, type) の列 */
static TvmRegInfo *rd_infos(Reader *r, uint32_t n) {
  if (!count_ok(r, n, 3))
    return NULL;
  TvmRegInfo *a = (TvmRegInfo *)calloc(n ? n : 1, sizeof(TvmRegInfo));
  for (uint32_t k = 0; k < n && a; k++) {
    a[k].kind = rd_u8(r);
    a[k].type = rd_u16(r);
  }
  return a;
}

static bool parse(TvmVm *vm, Reader *r) {
  if (rd_u32(r) != TVM_MAGIC) {
    tvm_set_error(vm, "not a .tcsb (magic)");
    return false;
  }
  if (rd_u32(r) != TVM_VERSION) {
    tvm_set_error(vm, ".tcsb version mismatch");
    return false;
  }
  if (rd_u32(r) != op_abi_hash()) {
    tvm_set_error(vm, ".tcsb op table mismatch (rebuild tcs2vm and tcsvm together)");
    return false;
  }

  /* 個数は表を確保してから vm に入れる (途中で失敗しても tvm_free が確保済みの分だけを解放する) */
  uint32_t n = rd_u32(r);
  if (!count_ok(r, n, 4))
    goto bad;
  vm->strings = (char **)calloc(n ? n : 1, sizeof(char *));
  vm->nstrings = n;
  for (uint32_t i = 0; i < vm->nstrings && !r->bad; i++) {
    uint32_t len = rd_u32(r);
    if (!count_ok(r, len, 1))
      goto bad;
    vm->strings[i] = (char *)malloc(len + 1);
    memcpy(vm->strings[i], r->p, len);
    vm->strings[i][len] = '\0';
    r->p += len;
  }

  /* string の定数 (literal)。heap の外に置き、VM の寿命の間生きる。型は types の後で付ける */
  n = rd_u32(r);
  if (!count_ok(r, n, 4))
    goto bad;
  vm->consts = (TvmString **)calloc(n ? n : 1, sizeof(TvmString *));
  vm->nconsts = n;
  for (uint32_t i = 0; i < vm->nconsts && !r->bad; i++) {
    uint32_t len = rd_u32(r);
    if (!count_ok(r, len, 1) || len > 0x7FFFFFFF)
      goto bad;
    TvmString *s = (TvmString *)tvm_alloc_fixed(sizeof(TvmString) + len + 1);
    if (!s)
      goto bad;
    vm->consts[i] = s;
    s->len = (int32_t)len;
    memcpy(STR_DATA(s), r->p, len);
    r->p += len;
  }

  /* 型の表 (VM と tcs2c が共有する形。runtime/model) */
  const char *terr = tcs_model_read(r, (const char *const *)vm->strings, vm->nstrings, &vm->ntypes, &vm->types);
  if (terr) {
    tvm_set_error(vm, "%s", terr);
    return false;
  }
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (vm->types[i].tkind == TVM_T_STRING && vm->string_type == TVM_NO_TYPE)
      vm->string_type = (uint16_t)i;
  vm->type_ids = (uint32_t *)calloc(vm->ntypes ? vm->ntypes : 1, sizeof(uint32_t));
  vm->iface_checks = (TvmIfaceCheck *)calloc(vm->ntypes ? vm->ntypes : 1, sizeof(TvmIfaceCheck));
  if (!vm->type_ids || !vm->iface_checks) {
    tvm_set_error(vm, "out of memory");
    return false;
  }
  if (!tvm_bind_host_types(vm))
    return false;

  n = rd_u32(r);
  if (!count_ok(r, n, 6))
    goto bad;
  vm->selectors = (TvmSelector *)calloc(n ? n : 1, sizeof(TvmSelector));
  vm->nselectors = n;
  for (uint32_t i = 0; i < n; i++) {
    vm->selectors[i].name = str_at(vm, rd_u32(r));
    vm->selectors[i].functype = rd_u16(r);
  }

  /* runtime の関数は名前で引く (VM が持たない名前は読み込みエラー) */
  n = rd_u32(r);
  if (!count_ok(r, n, 4))
    goto bad;
  vm->intrinsics = (const TvmIntrinsic **)calloc(n ? n : 1, sizeof(TvmIntrinsic *));
  vm->nintrinsics = n;
  for (uint32_t i = 0; i < n && !r->bad; i++) {
    const char *name = str_at(vm, rd_u32(r));
    size_t k = 0;
    while (k < tvm_nintrinsics && strcmp(tvm_intrinsics[k].name, name) != 0)
      k++;
    if (k < tvm_nintrinsics) {
      vm->intrinsics[i] = &tvm_intrinsics[k];
      continue;
    }
    for (k = 0; k < vm->nhosts && strcmp(vm->hosts[k].name, name) != 0; k++)
      ;
    if (k < vm->nhosts) {
      vm->intrinsics[i] = &vm->hosts[k];
      continue;
    }
    {
      tvm_set_error(vm, "unknown runtime function %s", name);
      return false;
    }

  }

  n = rd_u32(r);
  if (!count_ok(r, n, 3))
    goto bad;
  vm->static_info = rd_infos(r, n);
  vm->statics = (TvmSlot *)calloc(n ? n : 1, sizeof(TvmSlot));
  if (!vm->static_info)
    goto bad;
  vm->nstatics = n;
  n = rd_u32(r);
  if (!count_ok(r, n, 11))
    goto bad;
  vm->static_fields = (TvmStaticField *)calloc(n ? n : 1, sizeof(TvmStaticField));
  if (!vm->static_fields)
    goto bad;
  vm->nstatic_fields = n;
  for (uint32_t i = 0; i < n; i++) {
    vm->static_fields[i].name = str_at(vm, rd_u32(r));
    vm->static_fields[i].slot = rd_u32(r);
    vm->static_fields[i].kind = rd_u8(r);
    vm->static_fields[i].type = rd_u16(r);
  }

  n = rd_u32(r);
  if (!count_ok(r, n, 16))
    goto bad;
  vm->funcs = (TvmProto *)calloc(n ? n : 1, sizeof(TvmProto));
  vm->nfuncs = n;
  for (uint32_t i = 0; i < vm->nfuncs && !r->bad; i++) {
    TvmProto *f = &vm->funcs[i];
    f->name = str_at(vm, rd_u32(r));
    f->file = str_at(vm, rd_u32(r));
    f->nparams = rd_u16(r);
    f->nret = rd_u16(r);
    f->nregs = rd_u16(r);
    f->regs = rd_infos(r, f->nregs);
    f->rets = rd_infos(r, f->nret);
    f->ncaps = rd_u16(r);
    f->caps = rd_infos(r, f->ncaps);
    if (!f->regs || !f->rets || !f->caps)
      goto bad;
    f->ncode = rd_u32(r);
    if (!count_ok(r, f->ncode, 8))
      goto bad;
    f->code = (uint64_t *)calloc(f->ncode ? f->ncode : 1, sizeof(uint64_t));
    for (uint32_t k = 0; k < f->ncode; k++)
      f->code[k] = rd_u64(r);
    f->nlines = rd_u32(r);
    if (!count_ok(r, f->nlines, 8))
      goto bad;
    f->lines = (TvmLine *)calloc(f->nlines ? f->nlines : 1, sizeof(TvmLine));
    for (uint32_t k = 0; k < f->nlines; k++) {
      f->lines[k].pc = rd_u32(r);
      f->lines[k].line = rd_u32(r);
    }
  }
  vm->entry = (int32_t)rd_u32(r);
  if (r->bad)
    goto bad;
  /* 関数ごとの path と --trace の表 (tcsvm_debug.c) */
  if (!tvm_debug_read(vm, r)) {
    tvm_set_error(vm, "bad debug tables");
    return false;
  }
  return true;
bad:
  tvm_set_error(vm, "truncated or malformed .tcsb");
  return false;
}

/* ---- layout (VM が決める) ---- */

uint32_t tvm_kind_size(uint8_t kind) {
  switch (kind) {
  case TVM_K_I32:
  case TVM_K_F32:
    return 4;
  case TVM_K_BOOL:
    return 1;
  default:
    return (uint32_t)sizeof(void *);
  }
}

static uint32_t align_up(uint32_t v, uint32_t a) { return (v + a - 1) / a * a; }

static bool info_ok(TvmVm *vm, const TvmRegInfo *info) {
  if (info->kind > TVM_K_REF)
    return false;
  return info->kind != TVM_K_REF || info->type == TVM_NO_TYPE || info->type < vm->ntypes;
}

static bool layout(TvmVm *vm) {
  if (vm->string_type != TVM_NO_TYPE)
    for (uint32_t i = 0; i < vm->nconsts; i++)
      TVM_TYPE(vm->consts[i]) = &vm->types[vm->string_type];
  else if (vm->nconsts) {
    tvm_set_error(vm, "string constants without a string type");
    return false;
  }
  /* C の struct の規則 (doc/object-model.md §5)。class の payload は VM の型の pointer の後から
     (header を tcs2c と揃えるのは M-4) */
  vm->types_version = tvm_next_types_version();
  /* C の module の class (tvm_host_type に結んだ --ref の class) は C の module の layout で、host_value を持たない
     (host_value は host の data class だけ。doc/object-model.md §3) */
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (vm->types[i].tkind == TVM_T_CLASS && vm->type_ids[i] < TVM_TYPE_ID_VM)
      vm->types[i].flags &= (uint8_t)~TCS_TYPE_HOST_VALUE;
  const char *err = tcs_model_layout(vm->types, vm->ntypes, (uint32_t)sizeof(TvmObject), (uint32_t)sizeof(void *));
  if (err) {
    tvm_set_error(vm, "type table: %s", err);
    return false;
  }
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    TvmType *t = &vm->types[i];
    if (t->tkind == TVM_T_FUNC) {
      for (uint32_t k = 0; k < t->nparams; k++)
        if (!info_ok(vm, &t->params[k]))
          goto bad;
      for (uint32_t k = 0; k < t->nrets; k++)
        if (!info_ok(vm, &t->rets[k]))
          goto bad;
    }
    if (t->tkind == TVM_T_BOX && t->elem_kind > TVM_K_BOOL)
      goto bad;
    if (t->tkind == TVM_T_CLASS)
      for (uint32_t k = 0; k < t->nifaces; k++)
        if (t->ifaces[k] >= vm->ntypes ||
            (vm->types[t->ifaces[k]].tkind != TVM_T_IFACE && vm->types[t->ifaces[k]].tkind != TVM_T_OBJECT))
          goto bad;
    if (t->tkind == TVM_T_CLASS || t->tkind == TVM_T_STRUCT)
      for (uint32_t k = 0; k < t->nleaves; k++) {
        TvmRegInfo info = {t->leaves[k].kind, t->leaves[k].type};
        if (!info_ok(vm, &info))
          goto bad;
      }
    if (t->tkind == TVM_T_DICT) {
      t->dict_entry = (uint32_t)sizeof(TvmDictEntry) + align_up(t->elem_size, 8);
      /* key は 1 葉の値 (int / float / bool / ref) */
      TvmRegInfo key = {t->key_kind, t->key_type};
      if (!info_ok(vm, &key))
        goto bad;
    }
  }
  return true;
bad:
  tvm_set_error(vm, "malformed type table");
  return false;
}

/* ---- 検査 (doc/vm-design.md §9.4) と link ----
   register の種類が関数の中で固定なので、命令ごとに operand の種類表と照らすだけで済む。 */

typedef struct Verify {
  TvmVm *vm;
  TvmProto *f;
  uint32_t pc;
} Verify;

static bool vfail(Verify *v, const char *fmt, ...) {
  char msg[512];
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(msg, sizeof msg, fmt, ap);
  va_end(ap);
  tvm_set_error(v->vm, "verify %s pc %u: %s", v->f->name, v->pc, msg);
  return false;
}

static bool reg(Verify *v, uint32_t r, uint8_t kind) {
  if (r >= v->f->nregs)
    return vfail(v, "register r%u out of range", r);
  if (v->f->regs[r].kind != kind)
    return vfail(v, "register r%u has kind %u, want %u", r, v->f->regs[r].kind, kind);
  return true;
}

/* 型 from の値を型 to の場所に置けるか: 同じ型、to が object、to が interface で from が
   それを実装する class。TVM_NO_TYPE (null だけを持つ register) はどこにでも置ける */
static bool assignable(TvmVm *vm, uint16_t from, uint16_t to) {
  if (from == to || from == TVM_NO_TYPE)
    return true;
  if (to >= vm->ntypes || from >= vm->ntypes)
    return false;
  const TvmType *tt = &vm->types[to], *ft = &vm->types[from];
  if (tt->tkind == TVM_T_OBJECT)
    return true;
  if (tt->tkind == TVM_T_IFACE && ft->tkind == TVM_T_CLASS)
    for (uint32_t k = 0; k < ft->nifaces; k++)
      if (ft->ifaces[k] == to)
        return true;
  return false;
}

/* register r の値を型 type の場所に置けるか */
static bool ref_type(Verify *v, uint32_t r, uint16_t type) {
  if (!reg(v, r, TVM_K_REF))
    return false;
  uint16_t t = v->f->regs[r].type;
  if (!assignable(v->vm, t, type))
    return vfail(v, "register r%u has type %u, want %u", r, t, type);
  return true;
}

/* register の値が info (kind, type) に合う */
static bool value(Verify *v, uint32_t r, const TvmRegInfo *info) {
  return info->kind == TVM_K_REF ? ref_type(v, r, info->type) : reg(v, r, info->kind);
}

/* register に info の値を書ける (ref は register の型に置ける型。null だけの register には書けない) */
static bool dest(Verify *v, uint32_t r, const TvmRegInfo *info) {
  if (!reg(v, r, info->kind))
    return false;
  uint16_t t = v->f->regs[r].type;
  if (info->kind == TVM_K_REF && (t == TVM_NO_TYPE || !assignable(v->vm, info->type, t)))
    return vfail(v, "register r%u has type %u, want %u", r, t, info->type);
  return true;
}

static bool type_of_kind(TvmVm *vm, uint32_t t, uint8_t tkind) {
  return t < vm->ntypes && vm->types[t].tkind == tkind;
}

static const TvmType *reg_type(Verify *v, uint32_t r, uint8_t tkind);

/* CALLR の引数 / 戻り値の種類の 1 文字 (tcsvm_internal.h の TvmIntrinsic) */
static bool spec_ok(Verify *v, uint32_t r, char spec, uint16_t arg0type, bool out) {
  TvmVm *vm = v->vm;
  switch (spec) {
  case 'i':
    return reg(v, r, TVM_K_I32);
  case 'f':
    return reg(v, r, TVM_K_F32);
  case 'b':
    return reg(v, r, TVM_K_BOOL);
  case 's':
    return out ? dest(v, r, &(TvmRegInfo){TVM_K_REF, vm->string_type}) : ref_type(v, r, vm->string_type);
  case 'o':
    return reg(v, r, TVM_K_REF) && (!out || v->f->regs[r].type != TVM_NO_TYPE);
  case 'L':
  case 'A':
  case 'D':
  case 'F': {
    uint8_t tk = spec == 'L' ? TVM_T_LIST : spec == 'A' ? TVM_T_ARRAY : spec == 'D' ? TVM_T_DICT : TVM_T_FUNC;
    return reg_type(v, r, tk) != NULL;
  }
  case 'r':
    return out ? dest(v, r, &(TvmRegInfo){TVM_K_REF, arg0type}) : ref_type(v, r, arg0type);
  case 'k': {
    if (!type_of_kind(vm, arg0type, TVM_T_DICT))
      return vfail(v, "key of a non-dictionary");
    const TvmType *t = &vm->types[arg0type];
    TvmRegInfo info = {t->key_kind, t->key_type};
    return out ? dest(v, r, &info) : value(v, r, &info);
  }
  case 'e': {
    if (arg0type >= vm->ntypes)
      return vfail(v, "element of a non-collection");
    const TvmType *t = &vm->types[arg0type];
    if (t->elem_kind > TVM_K_REF)
      return vfail(v, "runtime function on struct elements");
    TvmRegInfo info = {t->elem_kind, t->elem_type};
    return out ? dest(v, r, &info) : value(v, r, &info);
  }
  default:
    return vfail(v, "bad runtime function spec");
  }
}

static const TvmType *reg_type(Verify *v, uint32_t r, uint8_t tkind) {
  if (!reg(v, r, TVM_K_REF))
    return NULL;
  uint16_t t = v->f->regs[r].type;
  if (t >= v->vm->ntypes || v->vm->types[t].tkind != tkind) {
    vfail(v, "register r%u is not of the expected type kind %u", r, tkind);
    return NULL;
  }
  return &v->vm->types[t];
}

static bool string_reg(Verify *v, uint32_t r) {
  return reg_type(v, r, TVM_T_STRING) != NULL;
}

static bool jump(Verify *v, uint64_t ins) {
  int64_t target = (int64_t)v->pc + 1 + OFF24_OF(ins);
  if (target < 0 || target >= (int64_t)v->f->ncode)
    return vfail(v, "jump target %lld out of range", (long long)target);
  return true;
}

/* 葉の番号を byte offset に書き換える (field の命令の link) */
static uint64_t with_c(uint64_t ins, uint32_t c) {
  return (ins & ~((uint64_t)0xFFFF << 40)) | ((uint64_t)c << 40);
}
static uint64_t with_x(uint64_t ins, uint32_t x) {
  return (ins & ~((uint64_t)0xFF << 56)) | ((uint64_t)x << 56);
}

/* 配列 / List / Dictionary の要素の (kind, type) */
static TvmRegInfo elem_info(const TvmType *t) {
  TvmRegInfo info = {t->elem_kind, t->elem_type};
  return info;
}

static TvmRegInfo key_info(const TvmType *t) {
  TvmRegInfo info = {t->key_kind, t->key_type};
  return info;
}

static const uint8_t ld_kind[] = {TVM_K_I32, TVM_K_F32, TVM_K_BOOL, TVM_K_REF};

/* 4 つ組の命令の種類 (I32 / F32 / B / REF) */
static uint8_t quad(uint32_t op, uint32_t first) {
  /* 呼ぶ側の範囲の誤りで表の外を読まないよう、外なら存在しない種類を返して検査を落とす */
  return op >= first && op - first < 4 ? ld_kind[op - first] : 0xFF;
}

/* closure を作れる関数か: 引数と戻り値が関数型 t と同じ並び */
static bool matches_func(const TvmProto *g, const TvmType *t) {
  if (g->nparams != t->nparams || g->nret != t->nrets)
    return false;
  for (uint32_t k = 0; k < t->nparams; k++)
    if (g->regs[k].kind != t->params[k].kind || g->regs[k].type != t->params[k].type)
      return false;
  for (uint32_t k = 0; k < t->nrets; k++)
    if (g->rets[k].kind != t->rets[k].kind || g->rets[k].type != t->rets[k].type)
      return false;
  return true;
}

/* 関数の頭 (引数・戻り値・捕捉・register の表)。命令の検査が呼び出し先の表を読むので、
   全関数の頭を先に検査する */
static bool verify_header(TvmVm *vm, TvmProto *f) {
  Verify v = {vm, f, 0};
  if (f->nparams > f->nregs)
    return vfail(&v, "params exceed registers");
  for (uint32_t k = 0; k < f->nregs; k++)
    if (!info_ok(vm, &f->regs[k]))
      return vfail(&v, "register r%u bad kind or type", k);
  for (uint32_t k = 0; k < f->nret; k++)
    if (!info_ok(vm, &f->rets[k]))
      return vfail(&v, "bad return type");
  for (uint32_t k = 0; k < f->ncaps; k++)
    if (!info_ok(vm, &f->caps[k]))
      return vfail(&v, "bad capture type");
  return true;
}

static bool verify_proto(TvmVm *vm, TvmProto *f) {
  Verify v = {vm, f, 0};
  /* 呼び出し時に null で埋める範囲 */
  f->zero_lo = f->zero_hi = f->nparams;
  for (uint32_t k = f->nparams; k < f->nregs; k++)
    if (f->regs[k].kind == TVM_K_REF) {
      if (f->zero_lo == f->zero_hi)
        f->zero_lo = (uint16_t)k;
      f->zero_hi = (uint16_t)(k + 1);
    }
  if (f->ncode == 0)
    return vfail(&v, "empty code");

  for (v.pc = 0; v.pc < f->ncode; v.pc++) {
    uint64_t ins = f->code[v.pc];
    uint32_t op = (uint32_t)(ins & 0xFF);
    uint32_t a = (uint32_t)(ins >> 8) & 0xFFFF;
    uint32_t b = (uint32_t)(ins >> 24) & 0xFFFF;
    uint32_t c = (uint32_t)(ins >> 40) & 0xFFFF;
    uint32_t x = (uint32_t)(ins >> 56) & 0xFF;
    uint32_t imm = (uint32_t)(ins >> 24);
    switch (op) {
    case TVM_OP_NOP:
    case TVM_OP_RET0:
      break;
    case TVM_OP_TRACE:
      if (!vm->debug || imm >= vm->debug->nlines)
        return vfail(&v, "trace line %u out of range", imm);
      break;
    case TVM_OP_TVAL: {
      if (!vm->debug || c >= vm->debug->nvals)
        return vfail(&v, "trace value %u out of range", c);
      const TvmTraceVal *tv = &vm->debug->vals[c];
      if (b >= tv->nleaves || a >= f->nregs || f->regs[a].kind != tvm_debug_val_leaf_kind(vm, tv, b))
        return vfail(&v, "trace value leaf does not match");
      break;
    }
    case TVM_OP_MOV:
      if (a >= f->nregs || b >= f->nregs)
        return vfail(&v, "register out of range");
      if (f->regs[a].kind != f->regs[b].kind)
        return vfail(&v, "MOV between kinds");
      if (f->regs[a].kind == TVM_K_REF && !ref_type(&v, b, f->regs[a].type))
        return false;
      break;
    case TVM_OP_LDK:
      if (a >= f->nregs || f->regs[a].kind == TVM_K_REF)
        return vfail(&v, "LDK into ref or out of range");
      if (f->regs[a].kind == TVM_K_BOOL && imm > 1)
        return vfail(&v, "LDK bool not 0/1");
      break;
    case TVM_OP_LDNULL:
    case TVM_OP_NNULL:
      if (!reg(&v, a, TVM_K_REF))
        return false;
      break;
    case TVM_OP_LDSTR:
      if (imm >= vm->nconsts)
        return vfail(&v, "string constant %u out of range", imm);
      if (!string_reg(&v, a))
        return false;
      break;
    case TVM_OP_ADD_I32: case TVM_OP_SUB_I32: case TVM_OP_MUL_I32: case TVM_OP_DIV_I32:
    case TVM_OP_REM_I32: case TVM_OP_AND_I32: case TVM_OP_OR_I32: case TVM_OP_XOR_I32:
    case TVM_OP_SHL_I32: case TVM_OP_SHR_I32: case TVM_OP_CADD_I32: case TVM_OP_CSUB_I32:
    case TVM_OP_CMUL_I32: case TVM_OP_CMP_I32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_I32) || !reg(&v, c, TVM_K_I32))
        return false;
      break;
    case TVM_OP_NEG_I32: case TVM_OP_BNOT_I32: case TVM_OP_ADDI_I32: case TVM_OP_CNEG_I32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_ADD_F32: case TVM_OP_SUB_F32: case TVM_OP_MUL_F32: case TVM_OP_DIV_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32) || !reg(&v, c, TVM_K_F32))
        return false;
      break;
    case TVM_OP_CMP_F32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_F32) || !reg(&v, c, TVM_K_F32))
        return false;
      break;
    case TVM_OP_NEG_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32))
        return false;
      break;
    case TVM_OP_FMOD_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32) || !reg(&v, c, TVM_K_F32))
        return false;
      break;
    case TVM_OP_AND_B: case TVM_OP_OR_B: case TVM_OP_NE_B:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_BOOL) || !reg(&v, c, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_NVAL:
      if (!reg(&v, a, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_I2F:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_F2I:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_F32))
        return false;
      break;
    case TVM_OP_NOT_B:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_EQ_I32: case TVM_OP_LT_I32: case TVM_OP_LE_I32:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_I32) || !reg(&v, c, TVM_K_I32))
        return false;
      break;
    case TVM_OP_EQ_F32: case TVM_OP_LT_F32: case TVM_OP_LE_F32:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_F32) || !reg(&v, c, TVM_K_F32))
        return false;
      break;
    case TVM_OP_EQ_B:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_BOOL) || !reg(&v, c, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_EQ_REF:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_REF) || !reg(&v, c, TVM_K_REF))
        return false;
      break;
    case TVM_OP_EQ_STR:
      if (!reg(&v, a, TVM_K_BOOL) || !ref_type(&v, b, vm->string_type) ||
          !ref_type(&v, c, vm->string_type))
        return false;
      break;
    case TVM_OP_JMP:
      if (!jump(&v, ins))
        return false;
      break;
    case TVM_OP_JT: case TVM_OP_JF:
      if (!reg(&v, a, TVM_K_BOOL) || !jump(&v, ins))
        return false;
      break;
    case TVM_OP_JNULL: case TVM_OP_JNN:
      if (!reg(&v, a, TVM_K_REF) || !jump(&v, ins))
        return false;
      break;
    case TVM_OP_JT_LT_I32: case TVM_OP_JF_LT_I32: case TVM_OP_JT_LE_I32:
    case TVM_OP_JF_LE_I32: case TVM_OP_JT_EQ_I32: case TVM_OP_JF_EQ_I32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_I32) || !jump(&v, ins))
        return false;
      break;
    case TVM_OP_JT_LT_F32: case TVM_OP_JF_LT_F32: case TVM_OP_JT_LE_F32:
    case TVM_OP_JF_LE_F32: case TVM_OP_JT_EQ_F32: case TVM_OP_JF_EQ_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32) || !jump(&v, ins))
        return false;
      break;
    case TVM_OP_LDS:
      if (imm >= vm->nstatics)
        return vfail(&v, "static %u out of range", imm);
      if (!dest(&v, a, &vm->static_info[imm]))
        return false;
      break;
    case TVM_OP_STS:
      if (imm >= vm->nstatics)
        return vfail(&v, "static %u out of range", imm);
      if (!value(&v, a, &vm->static_info[imm]))
        return false;
      break;
    case TVM_OP_NEW:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_CLASS)
        return vfail(&v, "NEW of non-class type %u", c);
      if (!dest(&v, a, &(TvmRegInfo){TVM_K_REF, (uint16_t)c}))
        return false;
      break;
    case TVM_OP_LDF_I32: case TVM_OP_LDF_F32: case TVM_OP_LDF_B: case TVM_OP_LDF_REF:
    case TVM_OP_STF_I32: case TVM_OP_STF_F32: case TVM_OP_STF_B: case TVM_OP_STF_REF: {
      bool load = op <= TVM_OP_LDF_REF;
      uint8_t kind = load ? quad(op, TVM_OP_LDF_I32) : quad(op, TVM_OP_STF_I32);
      uint32_t obj = load ? b : a, val = load ? a : b;
      const TvmType *t = reg_type(&v, obj, TVM_T_CLASS);
      if (!t)
        return false;
      if (c >= t->nleaves || t->leaves[c].kind != kind)
        return vfail(&v, "field %u of %s does not match", c, t->name);
      TvmRegInfo info = {kind, t->leaves[c].type};
      if (load ? !dest(&v, val, &info) : !value(&v, val, &info))
        return false;
      f->code[v.pc] = with_c(ins, t->leaves[c].offset);
      break;
    }
    case TVM_OP_NEWARR:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_ARRAY)
        return vfail(&v, "NEWARR of non-array type %u", c);
      if (!dest(&v, a, &(TvmRegInfo){TVM_K_REF, (uint16_t)c}) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_ARR_LEN:
      if (!reg(&v, a, TVM_K_I32) || !reg_type(&v, b, TVM_T_ARRAY))
        return false;
      break;
    case TVM_OP_LDE_I32: case TVM_OP_LDE_F32: case TVM_OP_LDE_B: case TVM_OP_LDE_REF:
    case TVM_OP_STE_I32: case TVM_OP_STE_F32: case TVM_OP_STE_B: case TVM_OP_STE_REF:
    case TVM_OP_LIST_LD_I32: case TVM_OP_LIST_LD_F32: case TVM_OP_LIST_LD_B:
    case TVM_OP_LIST_LD_REF: case TVM_OP_LIST_ST_I32: case TVM_OP_LIST_ST_F32:
    case TVM_OP_LIST_ST_B: case TVM_OP_LIST_ST_REF: {
      bool list = op >= TVM_OP_LIST_LD_I32;
      uint32_t first = list ? TVM_OP_LIST_LD_I32 : TVM_OP_LDE_I32;
      bool load = op < first + 4;
      uint8_t kind = quad(op, load ? first : first + 4);
      uint32_t coll = load ? b : a, idx = load ? c : b, val = load ? a : c;
      const TvmType *t = reg_type(&v, coll, list ? TVM_T_LIST : TVM_T_ARRAY);
      if (!t)
        return false;
      if (t->elem_kind != kind)
        return vfail(&v, "element kind of %s does not match", t->name);
      TvmRegInfo info = elem_info(t);
      if (!reg(&v, idx, TVM_K_I32) || (load ? !dest(&v, val, &info) : !value(&v, val, &info)))
        return false;
      break;
    }
    case TVM_OP_LDEF_I32: case TVM_OP_LDEF_F32: case TVM_OP_LDEF_B: case TVM_OP_LDEF_REF:
    case TVM_OP_STEF_I32: case TVM_OP_STEF_F32: case TVM_OP_STEF_B: case TVM_OP_STEF_REF: {
      bool load = op <= TVM_OP_LDEF_REF;
      uint8_t kind = load ? quad(op, TVM_OP_LDEF_I32) : quad(op, TVM_OP_STEF_I32);
      uint32_t arr = load ? b : a, idx = load ? c : b, val = load ? a : c;
      const TvmType *t = reg_type(&v, arr, TVM_T_ARRAY);
      if (!t)
        return false;
      if (t->elem_kind != TVM_K_STRUCT)
        return vfail(&v, "%s is not a struct array", t->name);
      const TvmType *s = &vm->types[t->elem_type];
      if (x >= s->nleaves || s->leaves[x].kind != kind)
        return vfail(&v, "leaf %u of %s does not match", x, s->name);
      if (s->leaves[x].offset > 0xFF)
        return vfail(&v, "leaf offset of %s exceeds 255", s->name);
      TvmRegInfo info = {kind, s->leaves[x].type};
      if (!reg(&v, idx, TVM_K_I32) || (load ? !dest(&v, val, &info) : !value(&v, val, &info)))
        return false;
      f->code[v.pc] = with_x(ins, s->leaves[x].offset);
      break;
    }
    case TVM_OP_NEWLIST:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_LIST)
        return vfail(&v, "NEWLIST of non-list type %u", c);
      if (!dest(&v, a, &(TvmRegInfo){TVM_K_REF, (uint16_t)c}))
        return false;
      break;
    case TVM_OP_LIST_LEN:
      if (!reg(&v, a, TVM_K_I32) || !reg_type(&v, b, TVM_T_LIST))
        return false;
      break;
    case TVM_OP_LIST_ADD_I32: case TVM_OP_LIST_ADD_F32: case TVM_OP_LIST_ADD_B:
    case TVM_OP_LIST_ADD_REF: {
      const TvmType *t = reg_type(&v, a, TVM_T_LIST);
      if (!t)
        return false;
      if (t->elem_kind != quad(op, TVM_OP_LIST_ADD_I32))
        return vfail(&v, "element kind of %s does not match", t->name);
      TvmRegInfo info = elem_info(t);
      if (!value(&v, b, &info))
        return false;
      break;
    }
    case TVM_OP_LIST_SORT: {
      const TvmType *t = reg_type(&v, a, TVM_T_LIST);
      const TvmType *ft = t ? reg_type(&v, b, TVM_T_FUNC) : NULL;
      if (!ft)
        return false;
      /* 比較は (要素の葉..., 要素の葉...) → int。struct の要素は葉に展開して渡す */
      TvmRegInfo one = elem_info(t);
      const TvmLeaf *leaves = NULL;
      uint32_t n = 1;
      if (t->elem_kind == TVM_K_STRUCT) {
        leaves = vm->types[t->elem_type].leaves;
        n = vm->types[t->elem_type].nleaves;
      }
      if (ft->nparams != 2 * n || ft->nrets != 1 || ft->rets[0].kind != TVM_K_I32)
        return vfail(&v, "sort comparison has a bad signature");
      for (uint32_t k = 0; k < 2 * n; k++) {
        TvmRegInfo want = leaves ? (TvmRegInfo){leaves[k % n].kind, leaves[k % n].type} : one;
        if (ft->params[k].kind != want.kind || ft->params[k].type != want.type)
          return vfail(&v, "sort comparison takes another element type");
      }
      break;
    }
    case TVM_OP_NEWDICT:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_DICT)
        return vfail(&v, "NEWDICT of non-dict type %u", c);
      if (!dest(&v, a, &(TvmRegInfo){TVM_K_REF, (uint16_t)c}))
        return false;
      break;
    case TVM_OP_DICT_LEN:
      if (!reg(&v, a, TVM_K_I32) || !reg_type(&v, b, TVM_T_DICT))
        return false;
      break;
    case TVM_OP_DICT_HAS:
    case TVM_OP_DICT_REMOVE:
    case TVM_OP_DICT_FIND:
    case TVM_OP_DICT_INS: {
      const TvmType *t = reg_type(&v, b, TVM_T_DICT);
      if (!t)
        return false;
      TvmRegInfo k = key_info(t);
      bool boolean = op == TVM_OP_DICT_HAS || op == TVM_OP_DICT_REMOVE;
      if (!reg(&v, a, boolean ? TVM_K_BOOL : TVM_K_I32) || !value(&v, c, &k))
        return false;
      break;
    }
    case TVM_OP_DICT_USED:
      if (!reg(&v, a, TVM_K_I32) || !reg_type(&v, b, TVM_T_DICT))
        return false;
      break;
    case TVM_OP_DICT_LIVE:
      if (!reg(&v, a, TVM_K_BOOL) || !reg_type(&v, b, TVM_T_DICT) || !reg(&v, c, TVM_K_I32))
        return false;
      break;
    case TVM_OP_DICT_LDK: {
      const TvmType *t = reg_type(&v, b, TVM_T_DICT);
      if (!t)
        return false;
      TvmRegInfo k = key_info(t);
      if (!dest(&v, a, &k) || !reg(&v, c, TVM_K_I32))
        return false;
      break;
    }
    case TVM_OP_DICT_LDV_I32: case TVM_OP_DICT_LDV_F32: case TVM_OP_DICT_LDV_B: case TVM_OP_DICT_LDV_REF:
    case TVM_OP_DICT_STV_I32: case TVM_OP_DICT_STV_F32: case TVM_OP_DICT_STV_B: case TVM_OP_DICT_STV_REF:
    case TVM_OP_LIST_LDF_I32: case TVM_OP_LIST_LDF_F32: case TVM_OP_LIST_LDF_B: case TVM_OP_LIST_LDF_REF:
    case TVM_OP_LIST_STF_I32: case TVM_OP_LIST_STF_F32: case TVM_OP_LIST_STF_B: case TVM_OP_LIST_STF_REF: {
      /* 要素 / 値の葉: 1 葉の型なら x = 0、struct なら struct の葉 x。x を byte offset に書き換える */
      bool dict = op >= TVM_OP_DICT_LDV_I32 && op <= TVM_OP_DICT_STV_REF;
      uint32_t first = dict ? TVM_OP_DICT_LDV_I32 : TVM_OP_LIST_LDF_I32;
      bool load = op < first + 4;
      uint8_t kind = quad(op, load ? first : first + 4);
      uint32_t coll = load ? b : a, idx = load ? c : b, val = load ? a : c;
      const TvmType *t = reg_type(&v, coll, dict ? TVM_T_DICT : TVM_T_LIST);
      if (!t)
        return false;
      TvmRegInfo info;
      uint32_t off;
      if (t->elem_kind == TVM_K_STRUCT) {
        const TvmType *st = &vm->types[t->elem_type];
        if (x >= st->nleaves || st->leaves[x].kind != kind)
          return vfail(&v, "leaf %u of %s does not match", x, st->name);
        info.kind = kind;
        info.type = st->leaves[x].type;
        off = st->leaves[x].offset;
      } else {
        if (x != 0 || t->elem_kind != kind)
          return vfail(&v, "element of %s does not match", t->name);
        info = elem_info(t);
        off = 0;
      }
      if (off > 0xFF)
        return vfail(&v, "leaf offset exceeds 255");
      if (!reg(&v, idx, TVM_K_I32) || (load ? !dest(&v, val, &info) : !value(&v, val, &info)))
        return false;
      f->code[v.pc] = with_x(ins, off);
      break;
    }
    case TVM_OP_LIST_PUSH0:
    case TVM_OP_LIST_CLEAR:
      if (!reg_type(&v, a, TVM_T_LIST))
        return false;
      break;
    case TVM_OP_LIST_INSERT0:
    case TVM_OP_LIST_REMOVE_AT:
      if (!reg_type(&v, a, TVM_T_LIST) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_ISTYPE:
      if (c >= vm->ntypes || !reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_REF))
        return vfail(&v, "bad ISTYPE");
      break;
    case TVM_OP_CAST:
    case TVM_OP_ASCAST:
      if (c >= vm->ntypes || !reg(&v, b, TVM_K_REF) ||
          !dest(&v, a, &(TvmRegInfo){TVM_K_REF, (uint16_t)c}))
        return vfail(&v, "bad CAST");
      break;
    case TVM_OP_BOX_I32: case TVM_OP_BOX_F32: case TVM_OP_BOX_B:
    case TVM_OP_UNBOX_I32: case TVM_OP_UNBOX_F32: case TVM_OP_UNBOX_B: {
      bool box = op <= TVM_OP_BOX_B;
      uint8_t kind = quad(op, box ? TVM_OP_BOX_I32 : TVM_OP_UNBOX_I32);
      if (!type_of_kind(vm, c, TVM_T_BOX) || vm->types[c].elem_kind != kind)
        return vfail(&v, "bad box type");
      if (box ? !(reg(&v, a, TVM_K_REF) && v.f->regs[a].type != TVM_NO_TYPE &&
                  assignable(vm, (uint16_t)c, v.f->regs[a].type) && reg(&v, b, kind))
              : !(reg(&v, a, kind) && reg(&v, b, TVM_K_REF)))
        return vfail(&v, "bad box operands");
      break;
    }
    case TVM_OP_EQ_REC:
      if (!reg(&v, a, TVM_K_BOOL) || !reg(&v, b, TVM_K_REF) || !reg(&v, c, TVM_K_REF))
        return false;
      break;
    case TVM_OP_CLONE:
      if (!reg_type(&v, b, TVM_T_CLASS) || !dest(&v, a, &(TvmRegInfo){TVM_K_REF, v.f->regs[b].type}))
        return false;
      break;
    case TVM_OP_CHR:
      if (!string_reg(&v, a) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_CALLR: {
      if (c >= vm->nintrinsics)
        return vfail(&v, "runtime function %u out of range", c);
      const TvmIntrinsic *in = vm->intrinsics[c];
      uint16_t arg0 = a < f->nregs ? f->regs[a].type : TVM_NO_TYPE;
      for (uint32_t k = 0; in->params[k]; k++)
        if (!spec_ok(&v, a + k, in->params[k], arg0, false))
          return vfail(&v, "argument %u of %s", k, in->name);
      for (uint32_t k = 0; in->rets[k]; k++)
        if (!spec_ok(&v, b + k, in->rets[k], arg0, true))
          return vfail(&v, "result %u of %s", k, in->name);
      break;
    }
    case TVM_OP_CALLI: {
      if (c >= vm->nselectors)
        return vfail(&v, "selector %u out of range", c);
      const TvmType *ft = &vm->types[vm->selectors[c].functype];
      if (!reg(&v, a, TVM_K_REF))
        return false;
      for (uint32_t k = 0; k < ft->nparams; k++)
        if (!value(&v, a + 1 + k, &ft->params[k]))
          return false;
      for (uint32_t k = 0; k < ft->nrets; k++)
        if (!dest(&v, b + k, &ft->rets[k]))
          return false;
      break;
    }
    case TVM_OP_CLOSURE: {
      if (c >= vm->nfuncs)
        return vfail(&v, "function %u out of range", c);
      const TvmProto *g = &vm->funcs[c];
      const TvmType *ft = reg_type(&v, a, TVM_T_FUNC);
      if (!ft)
        return false;
      if (!matches_func(g, ft))
        return vfail(&v, "%s does not match the function type %s", g->name, ft->name);
      for (uint32_t k = 0; k < g->ncaps; k++)
        if (!value(&v, b + k, &g->caps[k]))
          return false;
      break;
    }
    case TVM_OP_LDCAP:
      if (c >= f->ncaps)
        return vfail(&v, "capture %u out of range", c);
      if (!dest(&v, a, &f->caps[c]))
        return false;
      break;
    case TVM_OP_CALL: {
      if (c >= vm->nfuncs)
        return vfail(&v, "function %u out of range", c);
      const TvmProto *g = &vm->funcs[c];
      if (g->ncaps)
        return vfail(&v, "CALL of a function with captures (%s)", g->name);
      for (uint32_t k = 0; k < g->nparams; k++)
        if (!value(&v, a + k, &g->regs[k]))
          return false;
      for (uint32_t k = 0; k < g->nret; k++)
        if (!dest(&v, b + k, &g->rets[k]))
          return false;
      break;
    }
    case TVM_OP_CALLD: {
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_FUNC)
        return vfail(&v, "CALLD with non-function type %u", c);
      const TvmType *ft = &vm->types[c];
      if (!ref_type(&v, a, (uint16_t)c))
        return false;
      for (uint32_t k = 0; k < ft->nparams; k++)
        if (!value(&v, a + 1 + k, &ft->params[k]))
          return false;
      for (uint32_t k = 0; k < ft->nrets; k++)
        if (!dest(&v, b + k, &ft->rets[k]))
          return false;
      break;
    }
    case TVM_OP_RET:
      for (uint32_t k = 0; k < f->nret; k++)
        if (!value(&v, a + k, &f->rets[k]))
          return false;
      break;
    case TVM_OP_CONCAT:
      if (!string_reg(&v, a) || !ref_type(&v, b, vm->string_type) ||
          !ref_type(&v, c, vm->string_type))
        return false;
      break;
    case TVM_OP_I2S:
      if (!string_reg(&v, a) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_F2S:
      if (!string_reg(&v, a) || !reg(&v, b, TVM_K_F32))
        return false;
      break;
    case TVM_OP_B2S:
      if (!string_reg(&v, a) || !reg(&v, b, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_PRINT_I32:
      if (!reg(&v, a, TVM_K_I32))
        return false;
      break;
    case TVM_OP_PRINT_F32:
      if (!reg(&v, a, TVM_K_F32))
        return false;
      break;
    case TVM_OP_PRINT_B:
      if (!reg(&v, a, TVM_K_BOOL))
        return false;
      break;
    case TVM_OP_PRINT_S:
      if (!ref_type(&v, a, vm->string_type) || vm->string_type == TVM_NO_TYPE)
        return vfail(&v, "PRINT_S needs a string");
      break;
    default:
      return vfail(&v, "unknown op %u", op);
    }
    if (op == TVM_OP_RET && f->nret == 0)
      return vfail(&v, "RET in a void function");
    if (op == TVM_OP_RET0 && f->nret != 0)
      return vfail(&v, "RET0 in a function with results");
  }
  /* 最後の命令は先へ落ちない */
  uint32_t last = (uint32_t)(f->code[f->ncode - 1] & 0xFF);
  if (last != TVM_OP_RET && last != TVM_OP_RET0 && last != TVM_OP_JMP) {
    v.pc = f->ncode - 1;
    return vfail(&v, "code falls off the end");
  }
  for (uint32_t k = 0; k < f->nlines; k++)
    if (f->lines[k].pc >= f->ncode || (k > 0 && f->lines[k].pc < f->lines[k - 1].pc))
      return vfail(&v, "bad line table");
  return true;
}

/* ---- 入口 ---- */

TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size) {
  if (vm->nfuncs) {
    tvm_set_error(vm, "one module per VM (use tvm_apply to reload)");
    return TVM_LOAD_ERROR;
  }
  TvmStatus st = tvm_load_module(vm, bytes, size);
  if (st != TVM_OK)
    return st;
  /* static initializer: 名前が "<static>" で終わる関数を並びの順に走らせる */
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    const char *name = vm->funcs[i].name;
    size_t len = strlen(name);
    if (len >= 8 && strcmp(name + len - 8, "<static>") == 0) {
      st = tvm_call(vm, (int32_t)i, NULL, 0, NULL, 0);
      if (st != TVM_OK)
        return st;
    }
  }
  return TVM_OK;
}

TvmStatus tvm_load_module(TvmVm *vm, const uint8_t *bytes, size_t size) {
  Reader r = {bytes, bytes + size, false};
  if (!parse(vm, &r) || !layout(vm))
    return TVM_LOAD_ERROR;
  for (uint32_t i = 0; i < vm->nstatics; i++)
    if (!info_ok(vm, &vm->static_info[i])) {
      tvm_set_error(vm, "static %u: bad kind or type", i);
      return TVM_LOAD_ERROR;
    }
  for (uint32_t i = 0; i < vm->nfuncs; i++)
    if (!verify_header(vm, &vm->funcs[i]))
      return TVM_LOAD_ERROR;
  for (uint32_t i = 0; i < vm->nselectors; i++)
    if (!type_of_kind(vm, vm->selectors[i].functype, TVM_T_FUNC)) {
      tvm_set_error(vm, "selector %u: bad function type", i);
      return TVM_LOAD_ERROR;
    }
  /* itable: 関数は (class の self, 関数型の引数...) を取り、関数型の戻り値を返す */
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    const TvmType *t = &vm->types[i];
    for (uint32_t k = 0; t->tkind == TVM_T_CLASS && k < t->nitable; k++) {
      const TvmItable *it = &t->itable[k];
      bool ok = it->selector < vm->nselectors && it->func < vm->nfuncs &&
                (k == 0 || t->itable[k - 1].selector < it->selector);
      if (ok) {
        const TvmProto *g = &vm->funcs[it->func];
        const TvmType *ft = &vm->types[vm->selectors[it->selector].functype];
        ok = g->ncaps == 0 && g->nparams == ft->nparams + 1 && g->nret == ft->nrets &&
             g->regs[0].kind == TVM_K_REF && g->regs[0].type == i;
        for (uint32_t p = 0; ok && p < ft->nparams; p++)
          ok = g->regs[p + 1].kind == ft->params[p].kind && g->regs[p + 1].type == ft->params[p].type;
        for (uint32_t p = 0; ok && p < ft->nrets; p++)
          ok = g->rets[p].kind == ft->rets[p].kind && g->rets[p].type == ft->rets[p].type;
      }
      if (!ok) {
        tvm_set_error(vm, "type %s: bad itable entry %u", t->name, k);
        return TVM_LOAD_ERROR;
      }
    }
  }
  for (uint32_t i = 0; i < vm->nfuncs; i++)
    if (!verify_proto(vm, &vm->funcs[i]))
      return TVM_LOAD_ERROR;
  if (vm->entry != -1 && (vm->entry < 0 || (uint32_t)vm->entry >= vm->nfuncs)) {
    tvm_set_error(vm, "bad entry");
    return TVM_LOAD_ERROR;
  }
  /* static の field の表: slot と葉の数が static の種類表と合うこと */
  for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
    const TvmStaticField *f = &vm->static_fields[i];
    bool ok = f->slot < vm->nstatics;
    if (ok && f->kind == TVM_K_STRUCT) {
      ok = f->type < vm->ntypes && vm->types[f->type].tkind == TVM_T_STRUCT &&
           f->slot + vm->types[f->type].nleaves <= vm->nstatics;
      for (uint32_t k = 0; ok && k < vm->types[f->type].nleaves; k++)
        ok = vm->static_info[f->slot + k].kind == vm->types[f->type].leaves[k].kind;
    } else if (ok)
      ok = vm->static_info[f->slot].kind == f->kind;
    if (!ok) {
      tvm_set_error(vm, "static field %u: bad slot or type", i);
      return TVM_LOAD_ERROR;
    }
  }
  return TVM_OK;
}

int32_t tvm_find(TvmVm *vm, const char *name) {
  for (uint32_t i = 0; i < vm->nfuncs; i++)
    if (strcmp(vm->funcs[i].name, name) == 0)
      return (int32_t)i;
  return -1;
}

int32_t tvm_entry(TvmVm *vm) { return vm->entry; }

TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs, TvmSlot *rets,
                   size_t nrets) {
  if (func < 0 || (uint32_t)func >= vm->nfuncs) {
    tvm_set_error(vm, "no such function %d", func);
    return TVM_LOAD_ERROR;
  }
  const TvmProto *proto = &vm->funcs[func];
  if (nargs != proto->nparams || nrets < proto->nret || proto->ncaps) {
    tvm_set_error(vm, "%s takes %u argument slots and returns %u (given %zu and room for %zu)",
                  proto->name, proto->nparams, proto->nret, nargs, nrets);
    return TVM_LOAD_ERROR;
  }
  vm->faulting = false;
  /* shared heap: VM の呼び出しの間は C の module の関数 (host の関数) から戻っても回収しない */
  if (vm->heap)
    vm->heap->enter();
  TvmStatus st = tvm_run(vm, proto, args, rets);
  if (vm->heap)
    vm->heap->leave();
  return st;
}
