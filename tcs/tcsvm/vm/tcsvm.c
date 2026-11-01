/* tcs VM の読み込み (parse / 検査 / link) と interpreter。設計は doc/vm-design.md。
   C11 だけで書き、GNU 拡張は computed goto (#if で switch に切り替わる) だけに使う。 */
#include "tcsvm.h"

#include "tcsvm_ops.h"
#include "../../runtime/fmt/tcs_fmt.h"

#include <stdarg.h>
#include <stdbool.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define TVM_MAGIC 0x42534354u /* "TCSB" */
#define TVM_VERSION 1u

/* 分岐の相対 offset: bit 40..63 の符号付き 24 bit。signed の shift を使わずに符号拡張する */
#define OFF24_OF(ins) ((int32_t)((uint32_t)((ins) >> 40) ^ 0x800000u) - 0x800000)

/* ---- 型と関数 ---- */

typedef struct TvmLeaf {
  uint8_t kind;
  uint16_t type; /* ref の葉の型 (TVM_NO_TYPE は任意) */
  uint32_t offset; /* link が決める byte offset */
} TvmLeaf;

typedef struct TvmType {
  uint8_t tkind;
  const char *name;
  uint16_t nleaves;
  TvmLeaf *leaves;
  uint32_t size;  /* class: object の大きさ (header 込み)。struct: 値の大きさ */
  uint32_t align; /* struct の alignment */
  /* 配列 */
  uint8_t elem_kind; /* TVM_K_* か TVM_K_STRUCT */
  uint16_t elem_type;
  uint32_t elem_size;
} TvmType;

typedef struct TvmObject {
  const TvmType *type;
} TvmObject;

typedef struct TvmArray {
  const TvmType *type;
  int32_t len;
  int32_t pad;
} TvmArray;

#define ARR_DATA(a) ((char *)(a) + sizeof(TvmArray))
#define CLASS_HEADER ((uint32_t)sizeof(TvmObject))

typedef struct TvmRegInfo {
  uint8_t kind;
  uint16_t type;
} TvmRegInfo;

typedef struct TvmLine {
  uint32_t pc;
  uint32_t line;
} TvmLine;

typedef struct TvmProto {
  const char *name;
  uint16_t nparams;
  uint16_t nret;
  uint16_t nregs;
  uint16_t zero_lo, zero_hi; /* 呼び出し時に null で埋める ref register の範囲 */
  TvmRegInfo *regs;          /* regs[0..nparams) が引数、戻り値は先頭 nret 個の型 */
  TvmRegInfo *rets;
  uint32_t ncode;
  uint64_t *code;
  uint32_t nlines;
  TvmLine *lines;
  const char *file;
} TvmProto;

typedef struct TvmFrame {
  const uint64_t *pc;
  TvmSlot *R;
  const TvmProto *proto;
  uint32_t ret; /* 戻り値を書く caller の register */
} TvmFrame;

typedef struct TvmChunk {
  struct TvmChunk *next;
  size_t used, cap;
  /* 後ろに data */
} TvmChunk;

struct TvmVm {
  /* module */
  uint32_t nstrings;
  char **strings;
  uint32_t ntypes;
  TvmType *types;
  uint32_t nstatics;
  TvmRegInfo *static_info;
  TvmSlot *statics;
  uint32_t nfuncs;
  TvmProto *funcs;
  int32_t entry;
  /* 実行 */
  TvmSlot *stack, *stack_end;
  TvmFrame *frames, *frames_end;
  /* heap: V1 は bump 確保だけ (GC は V2。doc/vm-design.md §10) */
  TvmChunk *heap;
  size_t heap_bytes;
  char error[4096];
};

/* ---- エラー ---- */

static void set_error(TvmVm *vm, const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(vm->error, sizeof vm->error, fmt, ap);
  va_end(ap);
}

const char *tvm_error(TvmVm *vm) { return vm->error; }

/* ---- heap ---- */

static void *heap_alloc(TvmVm *vm, size_t size) {
  size = (size + 7u) & ~(size_t)7u;
  TvmChunk *c = vm->heap;
  if (!c || c->used + size > c->cap) {
    size_t cap = size > ((size_t)1 << 20) ? size : ((size_t)1 << 20);
    c = (TvmChunk *)malloc(sizeof(TvmChunk) + cap);
    if (!c)
      return NULL;
    c->next = vm->heap;
    c->used = 0;
    c->cap = cap;
    vm->heap = c;
  }
  void *p = (char *)(c + 1) + c->used;
  c->used += size;
  vm->heap_bytes += size;
  memset(p, 0, size);
  return p;
}

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
  vm->entry = -1;
  return vm;
}

void tvm_free(TvmVm *vm) {
  if (!vm)
    return;
  for (uint32_t i = 0; i < vm->nstrings; i++)
    free(vm->strings[i]);
  free(vm->strings);
  for (uint32_t i = 0; i < vm->ntypes; i++)
    free(vm->types[i].leaves);
  free(vm->types);
  free(vm->static_info);
  free(vm->statics);
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    free(vm->funcs[i].regs);
    free(vm->funcs[i].rets);
    free(vm->funcs[i].code);
    free(vm->funcs[i].lines);
  }
  free(vm->funcs);
  while (vm->heap) {
    TvmChunk *n = vm->heap->next;
    free(vm->heap);
    vm->heap = n;
  }
  free(vm->stack);
  free(vm->frames);
  free(vm);
}

/* ---- 読み込み: parse ---- */

typedef struct Reader {
  const uint8_t *p, *end;
  bool bad;
} Reader;

static uint8_t rd_u8(Reader *r) {
  if (r->end - r->p < 1) {
    r->bad = true;
    return 0;
  }
  return *r->p++;
}

static uint16_t rd_u16(Reader *r) {
  if (r->end - r->p < 2) {
    r->bad = true;
    return 0;
  }
  uint16_t v = (uint16_t)(r->p[0] | (r->p[1] << 8));
  r->p += 2;
  return v;
}

static uint32_t rd_u32(Reader *r) {
  if (r->end - r->p < 4) {
    r->bad = true;
    return 0;
  }
  uint32_t v = (uint32_t)r->p[0] | ((uint32_t)r->p[1] << 8) | ((uint32_t)r->p[2] << 16) |
               ((uint32_t)r->p[3] << 24);
  r->p += 4;
  return v;
}

static uint64_t rd_u64(Reader *r) {
  uint64_t lo = rd_u32(r);
  uint64_t hi = rd_u32(r);
  return lo | (hi << 32);
}

/* 個数の上限: 入力の残りの byte 数で抑え、巨大な確保を避ける */
static bool count_ok(Reader *r, uint32_t n, size_t unit) {
  if ((size_t)(r->end - r->p) / (unit ? unit : 1) < n) {
    r->bad = true;
    return false;
  }
  return true;
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

static bool parse(TvmVm *vm, Reader *r) {
  if (rd_u32(r) != TVM_MAGIC) {
    set_error(vm, "not a .tcsb (magic)");
    return false;
  }
  if (rd_u32(r) != TVM_VERSION) {
    set_error(vm, ".tcsb version mismatch");
    return false;
  }
  if (rd_u32(r) != op_abi_hash()) {
    set_error(vm, ".tcsb op table mismatch (rebuild tcs2vm and tcsvm together)");
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

  n = rd_u32(r);
  if (!count_ok(r, n, 4))
    goto bad;
  vm->types = (TvmType *)calloc(n ? n : 1, sizeof(TvmType));
  vm->ntypes = n;
  for (uint32_t i = 0; i < vm->ntypes && !r->bad; i++) {
    TvmType *t = &vm->types[i];
    t->tkind = rd_u8(r);
    t->name = str_at(vm, rd_u32(r));
    if (t->tkind == TVM_T_CLASS || t->tkind == TVM_T_STRUCT) {
      t->nleaves = rd_u16(r);
      if (!count_ok(r, t->nleaves, 3))
        goto bad;
      t->leaves = (TvmLeaf *)calloc(t->nleaves ? t->nleaves : 1, sizeof(TvmLeaf));
      for (uint32_t k = 0; k < t->nleaves; k++) {
        t->leaves[k].kind = rd_u8(r);
        t->leaves[k].type = rd_u16(r);
      }
    } else if (t->tkind == TVM_T_ARRAY) {
      t->elem_kind = rd_u8(r);
      t->elem_type = rd_u16(r);
    } else {
      set_error(vm, "type %u: bad kind %u", i, t->tkind);
      return false;
    }
  }

  n = rd_u32(r);
  if (!count_ok(r, n, 3))
    goto bad;
  vm->static_info = (TvmRegInfo *)calloc(n ? n : 1, sizeof(TvmRegInfo));
  vm->statics = (TvmSlot *)calloc(n ? n : 1, sizeof(TvmSlot));
  vm->nstatics = n;
  for (uint32_t i = 0; i < vm->nstatics; i++) {
    vm->static_info[i].kind = rd_u8(r);
    vm->static_info[i].type = rd_u16(r);
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
    if (!count_ok(r, f->nregs, 3))
      goto bad;
    f->regs = (TvmRegInfo *)calloc(f->nregs ? f->nregs : 1, sizeof(TvmRegInfo));
    for (uint32_t k = 0; k < f->nregs; k++) {
      f->regs[k].kind = rd_u8(r);
      f->regs[k].type = rd_u16(r);
    }
    if (!count_ok(r, f->nret, 3))
      goto bad;
    f->rets = (TvmRegInfo *)calloc(f->nret ? f->nret : 1, sizeof(TvmRegInfo));
    for (uint32_t k = 0; k < f->nret; k++) {
      f->rets[k].kind = rd_u8(r);
      f->rets[k].type = rd_u16(r);
    }
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
  return true;
bad:
  set_error(vm, "truncated or malformed .tcsb");
  return false;
}

/* ---- 読み込み: layout (doc/vm-design.md §9.3。VM が決める) ---- */

static uint32_t kind_size(uint8_t kind) {
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

static bool layout(TvmVm *vm) {
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    TvmType *t = &vm->types[i];
    if (t->tkind == TVM_T_ARRAY)
      continue;
    uint32_t off = t->tkind == TVM_T_CLASS ? CLASS_HEADER : 0;
    uint32_t align = 1;
    for (uint32_t k = 0; k < t->nleaves; k++) {
      TvmLeaf *l = &t->leaves[k];
      if (l->kind > TVM_K_REF) {
        set_error(vm, "type %s: bad leaf kind", t->name);
        return false;
      }
      if (l->kind == TVM_K_REF && l->type != TVM_NO_TYPE && l->type >= vm->ntypes) {
        set_error(vm, "type %s: bad leaf type", t->name);
        return false;
      }
      uint32_t s = kind_size(l->kind);
      off = align_up(off, s);
      l->offset = off;
      off += s;
      if (s > align)
        align = s;
    }
    t->align = align;
    t->size = align_up(off ? off : 1, t->tkind == TVM_T_CLASS ? 8 : align);
    if (t->size > 0xFFFF) {
      set_error(vm, "type %s: too large", t->name);
      return false;
    }
  }
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    TvmType *t = &vm->types[i];
    if (t->tkind != TVM_T_ARRAY)
      continue;
    if (t->elem_kind == TVM_K_STRUCT) {
      if (t->elem_type >= vm->ntypes || vm->types[t->elem_type].tkind != TVM_T_STRUCT) {
        set_error(vm, "type %s: bad struct element", t->name);
        return false;
      }
      t->elem_size = vm->types[t->elem_type].size;
    } else if (t->elem_kind <= TVM_K_REF) {
      if (t->elem_kind == TVM_K_REF && t->elem_type != TVM_NO_TYPE && t->elem_type >= vm->ntypes) {
        set_error(vm, "type %s: bad element type", t->name);
        return false;
      }
      t->elem_size = kind_size(t->elem_kind);
    } else {
      set_error(vm, "type %s: bad element kind", t->name);
      return false;
    }
  }
  return true;
}

/* ---- 読み込み: 検査 (doc/vm-design.md §9.4) と link ----
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
  set_error(v->vm, "verify %s pc %u: %s", v->f->name, v->pc, msg);
  return false;
}

static bool reg(Verify *v, uint32_t r, uint8_t kind) {
  if (r >= v->f->nregs)
    return vfail(v, "register r%u out of range", r);
  if (v->f->regs[r].kind != kind)
    return vfail(v, "register r%u has kind %u, want %u", r, v->f->regs[r].kind, kind);
  return true;
}

/* ref の型の一致: どちらかが任意 (TVM_NO_TYPE) なら通す */
static bool ref_type(Verify *v, uint32_t r, uint16_t type) {
  if (!reg(v, r, TVM_K_REF))
    return false;
  uint16_t t = v->f->regs[r].type;
  if (t != TVM_NO_TYPE && type != TVM_NO_TYPE && t != type)
    return vfail(v, "register r%u has type %u, want %u", r, t, type);
  return true;
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

static bool jump(Verify *v, uint64_t ins) {
  int32_t off = OFF24_OF(ins);
  int64_t target = (int64_t)v->pc + 1 + off;
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

static bool verify_proto(TvmVm *vm, TvmProto *f) {
  Verify v = {vm, f, 0};
  if (f->nparams > f->nregs || f->nret > f->nregs)
    return vfail(&v, "params / returns exceed registers");
  for (uint32_t k = 0; k < f->nregs; k++) {
    if (f->regs[k].kind > TVM_K_REF)
      return vfail(&v, "register r%u bad kind", k);
    if (f->regs[k].kind == TVM_K_REF && f->regs[k].type != TVM_NO_TYPE &&
        f->regs[k].type >= vm->ntypes)
      return vfail(&v, "register r%u bad type", k);
  }
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

  static const uint8_t ld_kind[] = {TVM_K_I32, TVM_K_F32, TVM_K_BOOL, TVM_K_REF};
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
      if (!reg(&v, a, TVM_K_REF))
        return false;
      break;
    case TVM_OP_ADD_I32: case TVM_OP_SUB_I32: case TVM_OP_MUL_I32: case TVM_OP_DIV_I32:
    case TVM_OP_REM_I32: case TVM_OP_AND_I32: case TVM_OP_OR_I32: case TVM_OP_XOR_I32:
    case TVM_OP_SHL_I32: case TVM_OP_SHR_I32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_I32) || !reg(&v, c, TVM_K_I32))
        return false;
      break;
    case TVM_OP_NEG_I32: case TVM_OP_BNOT_I32: case TVM_OP_ADDI_I32:
      if (!reg(&v, a, TVM_K_I32) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_ADD_F32: case TVM_OP_SUB_F32: case TVM_OP_MUL_F32: case TVM_OP_DIV_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32) || !reg(&v, c, TVM_K_F32))
        return false;
      break;
    case TVM_OP_NEG_F32:
      if (!reg(&v, a, TVM_K_F32) || !reg(&v, b, TVM_K_F32))
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
    case TVM_OP_JMP:
      if (!jump(&v, ins))
        return false;
      break;
    case TVM_OP_JT: case TVM_OP_JF:
      if (!reg(&v, a, TVM_K_BOOL) || !jump(&v, ins))
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
    case TVM_OP_LDS: case TVM_OP_STS:
      if (imm >= vm->nstatics)
        return vfail(&v, "static %u out of range", imm);
      if (vm->static_info[imm].kind == TVM_K_REF) {
        if (!ref_type(&v, a, vm->static_info[imm].type))
          return false;
      } else if (!reg(&v, a, vm->static_info[imm].kind)) {
        return false;
      }
      break;
    case TVM_OP_NEW:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_CLASS)
        return vfail(&v, "NEW of non-class type %u", c);
      if (!ref_type(&v, a, (uint16_t)c))
        return false;
      break;
    case TVM_OP_LDF_I32: case TVM_OP_LDF_F32: case TVM_OP_LDF_B: case TVM_OP_LDF_REF:
    case TVM_OP_STF_I32: case TVM_OP_STF_F32: case TVM_OP_STF_B: case TVM_OP_STF_REF: {
      bool load = op <= TVM_OP_LDF_REF;
      uint8_t kind = ld_kind[load ? op - TVM_OP_LDF_I32 : op - TVM_OP_STF_I32];
      uint32_t obj = load ? b : a, val = load ? a : b;
      const TvmType *t = reg_type(&v, obj, TVM_T_CLASS);
      if (!t)
        return false;
      if (c >= t->nleaves || t->leaves[c].kind != kind)
        return vfail(&v, "field %u of %s does not match", c, t->name);
      if (kind == TVM_K_REF ? !ref_type(&v, val, t->leaves[c].type) : !reg(&v, val, kind))
        return false;
      f->code[v.pc] = with_c(ins, t->leaves[c].offset);
      break;
    }
    case TVM_OP_NEWARR:
      if (c >= vm->ntypes || vm->types[c].tkind != TVM_T_ARRAY)
        return vfail(&v, "NEWARR of non-array type %u", c);
      if (!ref_type(&v, a, (uint16_t)c) || !reg(&v, b, TVM_K_I32))
        return false;
      break;
    case TVM_OP_ARR_LEN:
      if (!reg(&v, a, TVM_K_I32) || !reg_type(&v, b, TVM_T_ARRAY))
        return false;
      break;
    case TVM_OP_LDE_I32: case TVM_OP_LDE_F32: case TVM_OP_LDE_B: case TVM_OP_LDE_REF:
    case TVM_OP_STE_I32: case TVM_OP_STE_F32: case TVM_OP_STE_B: case TVM_OP_STE_REF: {
      bool load = op <= TVM_OP_LDE_REF;
      uint8_t kind = ld_kind[load ? op - TVM_OP_LDE_I32 : op - TVM_OP_STE_I32];
      uint32_t arr = load ? b : a, idx = load ? c : b, val = load ? a : c;
      const TvmType *t = reg_type(&v, arr, TVM_T_ARRAY);
      if (!t)
        return false;
      if (t->elem_kind != kind)
        return vfail(&v, "element kind of %s does not match", t->name);
      if (!reg(&v, idx, TVM_K_I32))
        return false;
      if (kind == TVM_K_REF ? !ref_type(&v, val, t->elem_type) : !reg(&v, val, kind))
        return false;
      break;
    }
    case TVM_OP_LDEF_I32: case TVM_OP_LDEF_F32: case TVM_OP_LDEF_B: case TVM_OP_LDEF_REF:
    case TVM_OP_STEF_I32: case TVM_OP_STEF_F32: case TVM_OP_STEF_B: case TVM_OP_STEF_REF: {
      bool load = op <= TVM_OP_LDEF_REF;
      uint8_t kind = ld_kind[load ? op - TVM_OP_LDEF_I32 : op - TVM_OP_STEF_I32];
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
        return vfail(&v, "leaf offset of %s exceeds 255 (V1 limit)", s->name);
      if (!reg(&v, idx, TVM_K_I32))
        return false;
      if (kind == TVM_K_REF ? !ref_type(&v, val, s->leaves[x].type) : !reg(&v, val, kind))
        return false;
      f->code[v.pc] = with_x(ins, s->leaves[x].offset);
      break;
    }
    case TVM_OP_CALL: {
      if (c >= vm->nfuncs)
        return vfail(&v, "function %u out of range", c);
      const TvmProto *g = &vm->funcs[c];
      for (uint32_t k = 0; k < g->nparams; k++) {
        const TvmRegInfo *p = &g->regs[k];
        if (p->kind == TVM_K_REF ? !ref_type(&v, a + k, p->type) : !reg(&v, a + k, p->kind))
          return false;
      }
      for (uint32_t k = 0; k < g->nret; k++) {
        const TvmRegInfo *p = &g->rets[k];
        if (b + k >= f->nregs || f->regs[b + k].kind != p->kind)
          return vfail(&v, "return value %u of %s does not match", k, g->name);
        if (p->kind == TVM_K_REF && p->type != TVM_NO_TYPE && f->regs[b + k].type != p->type &&
            f->regs[b + k].type != TVM_NO_TYPE)
          return vfail(&v, "return type of %s does not match", g->name);
      }
      break;
    }
    case TVM_OP_RET:
      for (uint32_t k = 0; k < f->nret; k++) {
        const TvmRegInfo *p = &f->rets[k];
        if (p->kind == TVM_K_REF ? !ref_type(&v, a + k, p->type) : !reg(&v, a + k, p->kind))
          return false;
      }
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

/* ---- fault と trace ---- */

static uint32_t line_of(const TvmProto *f, uint32_t pc) {
  uint32_t line = 0;
  for (uint32_t k = 0; k < f->nlines && f->lines[k].pc <= pc; k++)
    line = f->lines[k].line;
  return line;
}

static void append(char *buf, size_t cap, const char *fmt, ...) {
  size_t len = strlen(buf);
  if (len + 1 >= cap)
    return;
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(buf + len, cap - len, fmt, ap);
  va_end(ap);
}

static TvmStatus fault(TvmVm *vm, TvmFrame *base, TvmFrame *ci, const TvmProto *proto,
                       const uint64_t *pc, const char *what) {
  set_error(vm, "fault: %s", what);
  /* pc は命令の次を指している */
  uint32_t at = (uint32_t)(pc - proto->code) - 1;
  append(vm->error, sizeof vm->error, "\n  at %s (%s:%u)", proto->name, proto->file,
         line_of(proto, at));
  for (TvmFrame *fr = ci; fr > base; fr--) {
    const TvmProto *p = fr[-1].proto;
    uint32_t cat = (uint32_t)(fr[-1].pc - p->code) - 1;
    append(vm->error, sizeof vm->error, "\n  at %s (%s:%u)", p->name, p->file, line_of(p, cat));
  }
  return TVM_FAULT;
}

/* ---- interpreter ---- */

static void print_str(const char *s, size_t n) {
  fwrite(s, 1, n, stdout);
  fputc('\n', stdout);
}

#if defined(__GNUC__) && !defined(TVM_SWITCH_DISPATCH)
#define TVM_COMPUTED_GOTO 1
#endif

TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs, TvmSlot *rets,
                   size_t nrets) {
  if (func < 0 || (uint32_t)func >= vm->nfuncs) {
    set_error(vm, "no such function %d", func);
    return TVM_LOAD_ERROR;
  }
  const TvmProto *proto = &vm->funcs[func];
  if (nargs != proto->nparams || nrets < proto->nret) {
    set_error(vm, "%s takes %u argument slots and returns %u (given %zu and room for %zu)",
              proto->name, proto->nparams, proto->nret, nargs, nrets);
    return TVM_LOAD_ERROR;
  }
  /* V1 は入れ子の host 呼び出しを持たないので、stack の底から使う */
  TvmSlot *R = vm->stack;
  if (R + proto->nregs > vm->stack_end) {
    set_error(vm, "stack overflow");
    return TVM_FAULT;
  }
  for (uint32_t k = 0; k < proto->nparams; k++)
    R[k] = args[k];
  memset(R + proto->nparams, 0, sizeof(TvmSlot) * (size_t)(proto->nregs - proto->nparams));
  TvmFrame *const base = vm->frames;
  TvmFrame *ci = base;
  const uint64_t *pc = proto->code;
  uint64_t ins;

#define OPA ((uint32_t)(ins >> 8) & 0xFFFF)
#define OPB ((uint32_t)(ins >> 24) & 0xFFFF)
#define OPC ((uint32_t)(ins >> 40) & 0xFFFF)
#define OPX ((uint32_t)(ins >> 56))
#define IMM ((uint32_t)(ins >> 24))
#define OFF24 OFF24_OF(ins)
#define RA R[OPA]
#define RB R[OPB]
#define RC R[OPC]
#define FAULT(msg) return fault(vm, base, ci, proto, pc, msg)

#if TVM_COMPUTED_GOTO
  static const void *const labels[] = {
#define TVM_OP_LABEL(name) &&L_##name,
      TVM_OPS(TVM_OP_LABEL)
#undef TVM_OP_LABEL
  };
#define CASE(name) L_##name:
#define NEXT                                                                   \
  do {                                                                         \
    ins = *pc++;                                                               \
    goto *labels[ins & 0xFF];                                                  \
  } while (0)
  NEXT;
#else
#define CASE(name) case TVM_OP_##name:
#define NEXT continue
  for (;;) {
    ins = *pc++;
    switch ((uint32_t)(ins & 0xFF)) {
#endif

  CASE(NOP) NEXT;
  CASE(MOV) RA = RB; NEXT;
  CASE(LDK) RA.i = (int32_t)IMM; NEXT;
  CASE(LDNULL) RA.p = NULL; NEXT;
  CASE(ADD_I32) RA.i = (int32_t)((uint32_t)RB.i + (uint32_t)RC.i); NEXT;
  CASE(SUB_I32) RA.i = (int32_t)((uint32_t)RB.i - (uint32_t)RC.i); NEXT;
  CASE(MUL_I32) RA.i = (int32_t)((uint32_t)RB.i * (uint32_t)RC.i); NEXT;
  CASE(DIV_I32) {
    int32_t d = RC.i;
    if (d == 0)
      FAULT("division by zero");
    if (d == -1 && RB.i == INT32_MIN)
      FAULT("integer division overflow");
    RA.i = RB.i / d;
    NEXT;
  }
  CASE(REM_I32) {
    int32_t d = RC.i;
    if (d == 0)
      FAULT("division by zero");
    if (d == -1 && RB.i == INT32_MIN)
      FAULT("integer division overflow");
    RA.i = RB.i % d;
    NEXT;
  }
  CASE(AND_I32) RA.i = RB.i & RC.i; NEXT;
  CASE(OR_I32) RA.i = RB.i | RC.i; NEXT;
  CASE(XOR_I32) RA.i = RB.i ^ RC.i; NEXT;
  CASE(SHL_I32) RA.i = (int32_t)((uint32_t)RB.i << (RC.i & 31)); NEXT;
  CASE(SHR_I32) RA.i = RB.i >> (RC.i & 31); NEXT;
  CASE(NEG_I32) RA.i = (int32_t)(0u - (uint32_t)RB.i); NEXT;
  CASE(BNOT_I32) RA.i = ~RB.i; NEXT;
  CASE(ADDI_I32) RA.i = (int32_t)((uint32_t)RB.i + (uint32_t)(int32_t)(int16_t)OPC); NEXT;
  CASE(ADD_F32) RA.f = RB.f + RC.f; NEXT;
  CASE(SUB_F32) RA.f = RB.f - RC.f; NEXT;
  CASE(MUL_F32) RA.f = RB.f * RC.f; NEXT;
  CASE(DIV_F32) RA.f = RB.f / RC.f; NEXT;
  CASE(NEG_F32) RA.f = -RB.f; NEXT;
  CASE(I2F) RA.f = (float)RB.i; NEXT;
  CASE(F2I) {
    float v = RB.f;
    if (!(v > -2147483904.0f && v < 2147483648.0f))
      FAULT("float to int conversion out of range");
    RA.i = (int32_t)v;
    NEXT;
  }
  CASE(NOT_B) RA.i = !RB.i; NEXT;
  CASE(EQ_I32) RA.i = RB.i == RC.i; NEXT;
  CASE(LT_I32) RA.i = RB.i < RC.i; NEXT;
  CASE(LE_I32) RA.i = RB.i <= RC.i; NEXT;
  CASE(EQ_F32) RA.i = RB.f == RC.f; NEXT;
  CASE(LT_F32) RA.i = RB.f < RC.f; NEXT;
  CASE(LE_F32) RA.i = RB.f <= RC.f; NEXT;
  CASE(EQ_B) RA.i = RB.i == RC.i; NEXT;
  CASE(EQ_REF) RA.i = RB.p == RC.p; NEXT;
  CASE(JMP) pc += OFF24; NEXT;
  CASE(JT) if (RA.i) pc += OFF24; NEXT;
  CASE(JF) if (!RA.i) pc += OFF24; NEXT;
  CASE(JT_LT_I32) if (RA.i < RB.i) pc += OFF24; NEXT;
  CASE(JF_LT_I32) if (!(RA.i < RB.i)) pc += OFF24; NEXT;
  CASE(JT_LE_I32) if (RA.i <= RB.i) pc += OFF24; NEXT;
  CASE(JF_LE_I32) if (!(RA.i <= RB.i)) pc += OFF24; NEXT;
  CASE(JT_EQ_I32) if (RA.i == RB.i) pc += OFF24; NEXT;
  CASE(JF_EQ_I32) if (RA.i != RB.i) pc += OFF24; NEXT;
  CASE(JT_LT_F32) if (RA.f < RB.f) pc += OFF24; NEXT;
  CASE(JF_LT_F32) if (!(RA.f < RB.f)) pc += OFF24; NEXT;
  CASE(JT_LE_F32) if (RA.f <= RB.f) pc += OFF24; NEXT;
  CASE(JF_LE_F32) if (!(RA.f <= RB.f)) pc += OFF24; NEXT;
  CASE(JT_EQ_F32) if (RA.f == RB.f) pc += OFF24; NEXT;
  CASE(JF_EQ_F32) if (!(RA.f == RB.f)) pc += OFF24; NEXT;
  CASE(LDS) RA = vm->statics[IMM]; NEXT;
  CASE(STS) vm->statics[IMM] = RA; NEXT;
  CASE(NEW) {
    const TvmType *t = &vm->types[OPC];
    TvmObject *o = (TvmObject *)heap_alloc(vm, t->size);
    if (!o)
      FAULT("out of memory");
    o->type = t;
    RA.p = o;
    NEXT;
  }
#define LDF(T, field)                                                          \
  {                                                                            \
    char *o = (char *)RB.p;                                                    \
    if (!o)                                                                    \
      FAULT("null reference");                                                 \
    RA.field = *(T *)(o + OPC);                                                \
    NEXT;                                                                      \
  }
#define STF(T, field)                                                          \
  {                                                                            \
    char *o = (char *)RA.p;                                                    \
    if (!o)                                                                    \
      FAULT("null reference");                                                 \
    *(T *)(o + OPC) = (T)RB.field;                                             \
    NEXT;                                                                      \
  }
  CASE(LDF_I32) LDF(int32_t, i)
  CASE(LDF_F32) LDF(float, f)
  CASE(LDF_B) {
    char *o = (char *)RB.p;
    if (!o)
      FAULT("null reference");
    RA.i = *(uint8_t *)(o + OPC);
    NEXT;
  }
  CASE(LDF_REF) LDF(void *, p)
  CASE(STF_I32) STF(int32_t, i)
  CASE(STF_F32) STF(float, f)
  CASE(STF_B) STF(uint8_t, i)
  CASE(STF_REF) STF(void *, p)
  CASE(NEWARR) {
    int32_t n = RB.i;
    if (n < 0)
      FAULT("negative array size");
    const TvmType *t = &vm->types[OPC];
    if ((uint64_t)n * t->elem_size > ((uint64_t)1 << 31))
      FAULT("out of memory");
    TvmArray *arr = (TvmArray *)heap_alloc(vm, sizeof(TvmArray) + (size_t)n * t->elem_size);
    if (!arr)
      FAULT("out of memory");
    arr->type = t;
    arr->len = n;
    RA.p = arr;
    NEXT;
  }
  CASE(ARR_LEN) {
    TvmArray *arr = (TvmArray *)RB.p;
    if (!arr)
      FAULT("null reference");
    RA.i = arr->len;
    NEXT;
  }
#define ELEM(arrv, idxv, size)                                                 \
  TvmArray *arr = (TvmArray *)(arrv);                                          \
  if (!arr)                                                                    \
    FAULT("null reference");                                                   \
  uint32_t idx = (uint32_t)(idxv);                                             \
  if (idx >= (uint32_t)arr->len)                                               \
    FAULT("index out of range");                                               \
  char *e = ARR_DATA(arr) + (size_t)idx * (size);
  CASE(LDE_I32) { ELEM(RB.p, RC.i, 4) RA.i = *(int32_t *)e; NEXT; }
  CASE(LDE_F32) { ELEM(RB.p, RC.i, 4) RA.f = *(float *)e; NEXT; }
  CASE(LDE_B) { ELEM(RB.p, RC.i, 1) RA.i = *(uint8_t *)e; NEXT; }
  CASE(LDE_REF) { ELEM(RB.p, RC.i, sizeof(void *)) RA.p = *(void **)e; NEXT; }
  CASE(STE_I32) { ELEM(RA.p, RB.i, 4) *(int32_t *)e = RC.i; NEXT; }
  CASE(STE_F32) { ELEM(RA.p, RB.i, 4) *(float *)e = RC.f; NEXT; }
  CASE(STE_B) { ELEM(RA.p, RB.i, 1) *(uint8_t *)e = (uint8_t)RC.i; NEXT; }
  CASE(STE_REF) { ELEM(RA.p, RB.i, sizeof(void *)) *(void **)e = RC.p; NEXT; }
  CASE(LDEF_I32) { ELEM(RB.p, RC.i, arr->type->elem_size) RA.i = *(int32_t *)(e + OPX); NEXT; }
  CASE(LDEF_F32) { ELEM(RB.p, RC.i, arr->type->elem_size) RA.f = *(float *)(e + OPX); NEXT; }
  CASE(LDEF_B) { ELEM(RB.p, RC.i, arr->type->elem_size) RA.i = *(uint8_t *)(e + OPX); NEXT; }
  CASE(LDEF_REF) { ELEM(RB.p, RC.i, arr->type->elem_size) RA.p = *(void **)(e + OPX); NEXT; }
  CASE(STEF_I32) { ELEM(RA.p, RB.i, arr->type->elem_size) *(int32_t *)(e + OPX) = RC.i; NEXT; }
  CASE(STEF_F32) { ELEM(RA.p, RB.i, arr->type->elem_size) *(float *)(e + OPX) = RC.f; NEXT; }
  CASE(STEF_B) { ELEM(RA.p, RB.i, arr->type->elem_size) *(uint8_t *)(e + OPX) = (uint8_t)RC.i; NEXT; }
  CASE(STEF_REF) { ELEM(RA.p, RB.i, arr->type->elem_size) *(void **)(e + OPX) = RC.p; NEXT; }
  CASE(CALL) {
    /* 新しい frame は caller の frame の後ろ。引数は copy する (doc/vm-design.md §8.1。
       窓を重ねると caller の register の種類が呼び出しの後で崩れるため) */
    const TvmProto *g = &vm->funcs[OPC];
    TvmSlot *nR = R + proto->nregs;
    if (nR + g->nregs > vm->stack_end || ci + 1 >= vm->frames_end)
      FAULT("stack overflow");
    for (uint32_t k = 0; k < g->nparams; k++)
      nR[k] = R[OPA + k];
    if (g->zero_hi > g->zero_lo)
      memset(nR + g->zero_lo, 0, sizeof(TvmSlot) * (size_t)(g->zero_hi - g->zero_lo));
    ci->pc = pc;
    ci->R = R;
    ci->proto = proto;
    ci->ret = OPB;
    ci++;
    R = nR;
    proto = g;
    pc = g->code;
    NEXT;
  }
  CASE(RET) {
    uint32_t n = proto->nret;
    if (ci == base) {
      for (uint32_t k = 0; k < n; k++)
        rets[k] = R[OPA + k];
      return TVM_OK;
    }
    ci--;
    TvmSlot *dst = ci->R + ci->ret;
    for (uint32_t k = 0; k < n; k++)
      dst[k] = R[OPA + k];
    R = ci->R;
    pc = ci->pc;
    proto = ci->proto;
    NEXT;
  }
  CASE(RET0) {
    if (ci == base)
      return TVM_OK;
    ci--;
    R = ci->R;
    pc = ci->pc;
    proto = ci->proto;
    NEXT;
  }
  CASE(PRINT_I32) {
    char buf[16];
    int n = snprintf(buf, sizeof buf, "%d", RA.i);
    print_str(buf, (size_t)n);
    NEXT;
  }
  CASE(PRINT_F32) {
    char buf[TCS_FORMAT_F32_MAX];
    size_t n = tcs_format_f32(RA.f, buf);
    print_str(buf, n);
    NEXT;
  }
  CASE(PRINT_B) {
    if (RA.i)
      print_str("True", 4);
    else
      print_str("False", 5);
    NEXT;
  }

#if !TVM_COMPUTED_GOTO
    default:
      FAULT("bad opcode");
    }
  }
#endif
}

/* ---- 読み込みの入口 ---- */

TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size) {
  if (vm->nfuncs) {
    set_error(vm, "V1 loads one module per VM (reload is V4)");
    return TVM_LOAD_ERROR;
  }
  Reader r = {bytes, bytes + size, false};
  if (!parse(vm, &r) || !layout(vm))
    return TVM_LOAD_ERROR;
  for (uint32_t i = 0; i < vm->nstatics; i++) {
    if (vm->static_info[i].kind > TVM_K_REF) {
      set_error(vm, "static %u: bad kind", i);
      return TVM_LOAD_ERROR;
    }
  }
  for (uint32_t i = 0; i < vm->nfuncs; i++)
    if (!verify_proto(vm, &vm->funcs[i]))
      return TVM_LOAD_ERROR;
  if (vm->entry != -1 && (vm->entry < 0 || (uint32_t)vm->entry >= vm->nfuncs)) {
    set_error(vm, "bad entry");
    return TVM_LOAD_ERROR;
  }
  /* static initializer: 名前が "<static>" で終わる関数を並びの順に走らせる */
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    const char *name = vm->funcs[i].name;
    size_t len = strlen(name);
    if (len >= 8 && strcmp(name + len - 8, "<static>") == 0) {
      if (vm->funcs[i].nparams != 0 || vm->funcs[i].nret != 0) {
        set_error(vm, "%s: static initializer has a signature", name);
        return TVM_LOAD_ERROR;
      }
      TvmStatus st = tvm_call(vm, (int32_t)i, NULL, 0, NULL, 0);
      if (st != TVM_OK)
        return st;
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
