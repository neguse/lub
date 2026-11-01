/* tcs VM の interpreter。C11 だけで書き、GNU 拡張は computed goto (#if で switch に切り替わる) だけに使う。
   入れ子に入れる (runtime の List.Sort が比較の closure を呼ぶ)。入れ子の frame は vm->top / vm->ftop から積む。 */
#include "tcsvm_internal.h"

#include "../../runtime/fmt/tcs_fmt.h"

#include <stdio.h>
#include <string.h>

/* ---- fault と trace ---- */

static uint32_t line_of(const TvmProto *f, uint32_t pc) {
  uint32_t line = 0;
  for (uint32_t k = 0; k < f->nlines && f->lines[k].pc <= pc; k++)
    line = f->lines[k].line;
  return line;
}

/* what が NULL なら、入れ子の中で書いた message に、この run の frame を足すだけ */
static TvmStatus fault(TvmVm *vm, TvmFrame *base, TvmFrame *ci, const TvmProto *proto,
                       const uint64_t *pc, const char *what) {
  if (what)
    tvm_set_error(vm, "fault: %s", what);
  vm->faulting = true;
  /* pc は命令の次を指している */
  uint32_t at = (uint32_t)(pc - proto->code) - 1;
  tvm_append_error(vm, "\n  at %s (%s:%u)", proto->name, proto->file, line_of(proto, at));
  for (TvmFrame *fr = ci; fr > base; fr--) {
    const TvmProto *p = fr[-1].proto;
    uint32_t cat = (uint32_t)(fr[-1].pc - p->code) - 1;
    tvm_append_error(vm, "\n  at %s (%s:%u)", p->name, p->file, line_of(p, cat));
  }
  return TVM_FAULT;
}

static void print_line(const char *s, size_t n) {
  fwrite(s, 1, n, stdout);
  fputc('\n', stdout);
}

#if defined(__GNUC__) && !defined(TVM_SWITCH_DISPATCH)
#define TVM_COMPUTED_GOTO 1
#endif

static TvmStatus run(TvmVm *vm, const TvmProto *proto, const TvmClosure *clo, const TvmSlot *args,
                     TvmSlot *rets) {
  TvmSlot *const top0 = vm->top;
  TvmFrame *const base = vm->ftop;
  TvmSlot *R = top0;
  if (R + proto->nregs > vm->stack_end || base >= vm->frames_end) {
    tvm_set_error(vm, "fault: stack overflow");
    vm->faulting = true;
    return TVM_FAULT;
  }
  for (uint32_t k = 0; k < proto->nparams; k++)
    R[k] = args[k];
  memset(R + proto->nparams, 0, sizeof(TvmSlot) * (size_t)(proto->nregs - proto->nparams));
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
#define LEAVE(st)                                                              \
  do {                                                                         \
    vm->top = top0;                                                            \
    vm->ftop = base;                                                           \
    return (st);                                                               \
  } while (0)
#define FAULT(msg)                                                             \
  do {                                                                         \
    TvmStatus fst_ = fault(vm, base, ci, proto, pc, msg);                      \
    LEAVE(fst_);                                                               \
  } while (0)
/* 今の frame を ftop の下に保存する (GC の root と、入れ子の呼び出しの土台) */
#define SAVE()                                                                 \
  do {                                                                         \
    ci->pc = pc;                                                               \
    ci->R = R;                                                                 \
    ci->proto = proto;                                                         \
    ci->clo = clo;                                                             \
    vm->ftop = ci + 1;                                                         \
    vm->top = R + proto->nregs;                                                \
  } while (0)
/* 確保の地点: 溜まった確保が閾値を超えていれば、frame を保存して回収する */
#define SAFEPOINT()                                                            \
  do {                                                                         \
    if (vm->gc_debt >= vm->gc_limit) {                                         \
      SAVE();                                                                  \
      tvm_gc_collect(vm);                                                      \
    }                                                                          \
  } while (0)
#define NONNULL(p)                                                             \
  if (!(p))                                                                    \
  FAULT("null reference")

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
  CASE(LDSTR) RA.p = vm->consts[IMM]; NEXT;
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
#define CHECKED(expr)                                                          \
  {                                                                            \
    int64_t r_ = (expr);                                                       \
    if (r_ < INT32_MIN || r_ > INT32_MAX)                                      \
      FAULT("arithmetic overflow");                                            \
    RA.i = (int32_t)r_;                                                        \
    NEXT;                                                                      \
  }
  CASE(CADD_I32) CHECKED((int64_t)RB.i + RC.i)
  CASE(CSUB_I32) CHECKED((int64_t)RB.i - RC.i)
  CASE(CMUL_I32) CHECKED((int64_t)RB.i * RC.i)
  CASE(CNEG_I32) CHECKED(-(int64_t)RB.i)
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
  CASE(EQ_STR) RA.i = tvm_str_eq((const TvmString *)RB.p, (const TvmString *)RC.p); NEXT;
  CASE(CMP_I32) RA.i = (RB.i > RC.i) - (RB.i < RC.i); NEXT;
  CASE(CMP_F32) {
    float x = RB.f, y = RC.f;
    if (x < y)
      RA.i = -1;
    else if (x > y)
      RA.i = 1;
    else if (x == y)
      RA.i = 0;
    else if (x != x)
      RA.i = y != y ? 0 : -1;
    else
      RA.i = 1;
    NEXT;
  }
  CASE(JMP) pc += OFF24; NEXT;
  CASE(JT) if (RA.i) pc += OFF24; NEXT;
  CASE(JF) if (!RA.i) pc += OFF24; NEXT;
  CASE(JNULL) if (!RA.p) pc += OFF24; NEXT;
  CASE(JNN) if (RA.p) pc += OFF24; NEXT;
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
    SAFEPOINT();
    const TvmType *t = &vm->types[OPC];
    TvmObject *o = (TvmObject *)tvm_alloc(vm, t->size);
    if (!o)
      FAULT("out of memory");
    o->type = t;
    RA.p = o;
    NEXT;
  }
  CASE(NNULL) {
    NONNULL(RA.p);
    NEXT;
  }
#define LDF(T, field)                                                          \
  {                                                                            \
    char *o = (char *)RB.p;                                                    \
    NONNULL(o);                                                                \
    RA.field = *(T *)(o + OPC);                                                \
    NEXT;                                                                      \
  }
#define STF(T, field)                                                          \
  {                                                                            \
    char *o = (char *)RA.p;                                                    \
    NONNULL(o);                                                                \
    *(T *)(o + OPC) = (T)RB.field;                                             \
    NEXT;                                                                      \
  }
  CASE(LDF_I32) LDF(int32_t, i)
  CASE(LDF_F32) LDF(float, f)
  CASE(LDF_B) LDF(uint8_t, i)
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
    SAFEPOINT();
    TvmArray *arr = (TvmArray *)tvm_alloc(vm, sizeof(TvmArray) + (size_t)n * t->elem_size);
    if (!arr)
      FAULT("out of memory");
    arr->type = t;
    arr->len = n;
    RA.p = arr;
    NEXT;
  }
  CASE(ARR_LEN) {
    TvmArray *arr = (TvmArray *)RB.p;
    NONNULL(arr);
    RA.i = arr->len;
    NEXT;
  }
#define ELEM(arrv, idxv, size)                                                 \
  TvmArray *arr = (TvmArray *)(arrv);                                          \
  NONNULL(arr);                                                                \
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
  CASE(NEWLIST) {
    SAFEPOINT();
    TvmList *l = tvm_list_new(vm, &vm->types[OPC]);
    if (!l)
      FAULT("out of memory");
    RA.p = l;
    NEXT;
  }
  CASE(LIST_LEN) {
    TvmList *l = (TvmList *)RB.p;
    NONNULL(l);
    RA.i = l->count;
    NEXT;
  }
#define LELEM(listv, idxv, size)                                               \
  TvmList *l = (TvmList *)(listv);                                             \
  NONNULL(l);                                                                  \
  uint32_t idx = (uint32_t)(idxv);                                             \
  if (idx >= (uint32_t)l->count)                                               \
    FAULT("index out of range");                                               \
  char *e = l->data + (size_t)idx * (size);
  CASE(LIST_LD_I32) { LELEM(RB.p, RC.i, 4) RA.i = *(int32_t *)e; NEXT; }
  CASE(LIST_LD_F32) { LELEM(RB.p, RC.i, 4) RA.f = *(float *)e; NEXT; }
  CASE(LIST_LD_B) { LELEM(RB.p, RC.i, 1) RA.i = *(uint8_t *)e; NEXT; }
  CASE(LIST_LD_REF) { LELEM(RB.p, RC.i, sizeof(void *)) RA.p = *(void **)e; NEXT; }
  CASE(LIST_ST_I32) { LELEM(RA.p, RB.i, 4) *(int32_t *)e = RC.i; NEXT; }
  CASE(LIST_ST_F32) { LELEM(RA.p, RB.i, 4) *(float *)e = RC.f; NEXT; }
  CASE(LIST_ST_B) { LELEM(RA.p, RB.i, 1) *(uint8_t *)e = (uint8_t)RC.i; NEXT; }
  CASE(LIST_ST_REF) { LELEM(RA.p, RB.i, sizeof(void *)) *(void **)e = RC.p; NEXT; }
#define LADD(T, field)                                                         \
  {                                                                            \
    TvmList *l = (TvmList *)RA.p;                                              \
    NONNULL(l);                                                                \
    if (l->count == l->cap)                                                    \
      SAFEPOINT();                                                             \
    if (l->count == l->cap && !tvm_list_grow(vm, l))                           \
      FAULT("out of memory");                                                  \
    ((T *)l->data)[l->count++] = (T)RB.field;                                  \
    NEXT;                                                                      \
  }
  CASE(LIST_ADD_I32) LADD(int32_t, i)
  CASE(LIST_ADD_F32) LADD(float, f)
  CASE(LIST_ADD_B) LADD(uint8_t, i)
  CASE(LIST_ADD_REF) LADD(void *, p)
  CASE(LIST_SORT) {
    TvmList *l = (TvmList *)RA.p;
    NONNULL(l);
    NONNULL(RB.p);
    /* 入れ子の呼び出しはこの frame の後ろから積む。この frame は GC の root として保存する */
    SAVE();
    TvmStatus st = tvm_list_sort(vm, l, (const TvmClosure *)RB.p);
    if (st != TVM_OK)
      FAULT(NULL);
    NEXT;
  }
  CASE(NEWDICT) {
    SAFEPOINT();
    TvmDict *d = tvm_dict_new(vm, &vm->types[OPC]);
    if (!d)
      FAULT("out of memory");
    RA.p = d;
    NEXT;
  }
  CASE(DICT_LEN) {
    TvmDict *d = (TvmDict *)RB.p;
    NONNULL(d);
    RA.i = d->count;
    NEXT;
  }
#define DKEY(dv, keyv)                                                         \
  TvmDict *d = (TvmDict *)(dv);                                                \
  NONNULL(d);                                                                  \
  TvmSlot key = (keyv);                                                        \
  if (d->type->key_kind == TVM_K_REF && !key.p)                                \
    FAULT("null key");
#define DGET(field)                                                            \
  {                                                                            \
    DKEY(RB.p, RC)                                                             \
    TvmDictEntry *en = tvm_dict_find(d, key);                                  \
    if (!en)                                                                   \
      FAULT("key not found");                                                  \
    RA.field = en->value.field;                                                \
    NEXT;                                                                      \
  }
#define DSET(field)                                                            \
  {                                                                            \
    SAFEPOINT();                                                               \
    DKEY(RA.p, RB)                                                             \
    TvmDictEntry *en = tvm_dict_insert(vm, d, key);                            \
    if (!en)                                                                   \
      FAULT("out of memory");                                                  \
    en->value.u = 0;                                                           \
    en->value.field = RC.field;                                                \
    NEXT;                                                                      \
  }
  CASE(DICT_GET_I32) DGET(i)
  CASE(DICT_GET_F32) DGET(f)
  CASE(DICT_GET_B) DGET(i)
  CASE(DICT_GET_REF) DGET(p)
  CASE(DICT_SET_I32) DSET(i)
  CASE(DICT_SET_F32) DSET(f)
  CASE(DICT_SET_B) DSET(i)
  CASE(DICT_SET_REF) DSET(p)
  CASE(DICT_HAS) {
    DKEY(RB.p, RC)
    RA.i = tvm_dict_find(d, key) != NULL;
    NEXT;
  }
  CASE(CLOSURE) {
    SAFEPOINT();
    const TvmProto *g = &vm->funcs[OPC];
    TvmClosure *c = (TvmClosure *)tvm_alloc(vm, sizeof(TvmClosure) + sizeof(TvmSlot) * g->ncaps);
    if (!c)
      FAULT("out of memory");
    c->type = &vm->types[proto->regs[OPA].type];
    c->proto = g;
    c->ncaps = g->ncaps;
    for (uint32_t k = 0; k < g->ncaps; k++)
      CLO_CAPS(c)[k] = R[OPB + k];
    RA.p = c;
    NEXT;
  }
  CASE(LDCAP) RA = CLO_CAPS(clo)[OPC]; NEXT;
  CASE(CALL) {
    /* 新しい frame は caller の frame の後ろ。引数は copy する (doc/vm-design.md §8.1) */
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
    ci->clo = clo;
    ci->ret = OPB;
    ci++;
    R = nR;
    proto = g;
    clo = NULL;
    pc = g->code;
    NEXT;
  }
  CASE(CALLD) {
    const TvmClosure *c = (const TvmClosure *)RA.p;
    NONNULL(c);
    const TvmProto *g = c->proto;
    TvmSlot *nR = R + proto->nregs;
    if (nR + g->nregs > vm->stack_end || ci + 1 >= vm->frames_end)
      FAULT("stack overflow");
    for (uint32_t k = 0; k < g->nparams; k++)
      nR[k] = R[OPA + 1 + k];
    if (g->zero_hi > g->zero_lo)
      memset(nR + g->zero_lo, 0, sizeof(TvmSlot) * (size_t)(g->zero_hi - g->zero_lo));
    ci->pc = pc;
    ci->R = R;
    ci->proto = proto;
    ci->clo = clo;
    ci->ret = OPB;
    ci++;
    R = nR;
    proto = g;
    clo = c;
    pc = g->code;
    NEXT;
  }
  CASE(RET) {
    uint32_t n = proto->nret;
    if (ci == base) {
      for (uint32_t k = 0; k < n; k++)
        rets[k] = R[OPA + k];
      LEAVE(TVM_OK);
    }
    ci--;
    TvmSlot *dst = ci->R + ci->ret;
    for (uint32_t k = 0; k < n; k++)
      dst[k] = R[OPA + k];
    R = ci->R;
    pc = ci->pc;
    proto = ci->proto;
    clo = ci->clo;
    NEXT;
  }
  CASE(RET0) {
    if (ci == base)
      LEAVE(TVM_OK);
    ci--;
    R = ci->R;
    pc = ci->pc;
    proto = ci->proto;
    clo = ci->clo;
    NEXT;
  }
  CASE(CONCAT) {
    SAFEPOINT();
    TvmString *s = tvm_str_concat(vm, (const TvmString *)RB.p, (const TvmString *)RC.p);
    if (!s)
      FAULT("out of memory");
    RA.p = s;
    NEXT;
  }
  CASE(I2S) {
    SAFEPOINT();
    char buf[16];
    int n = snprintf(buf, sizeof buf, "%d", RB.i);
    TvmString *s = tvm_str_new(vm, buf, (size_t)n);
    if (!s)
      FAULT("out of memory");
    RA.p = s;
    NEXT;
  }
  CASE(F2S) {
    SAFEPOINT();
    char buf[TCS_FORMAT_F32_MAX];
    size_t n = tcs_format_f32(RB.f, buf);
    TvmString *s = tvm_str_new(vm, buf, n);
    if (!s)
      FAULT("out of memory");
    RA.p = s;
    NEXT;
  }
  CASE(B2S) {
    SAFEPOINT();
    TvmString *s = RB.i ? tvm_str_new(vm, "True", 4) : tvm_str_new(vm, "False", 5);
    if (!s)
      FAULT("out of memory");
    RA.p = s;
    NEXT;
  }
  CASE(PRINT_I32) {
    char buf[16];
    int n = snprintf(buf, sizeof buf, "%d", RA.i);
    print_line(buf, (size_t)n);
    NEXT;
  }
  CASE(PRINT_F32) {
    char buf[TCS_FORMAT_F32_MAX];
    size_t n = tcs_format_f32(RA.f, buf);
    print_line(buf, n);
    NEXT;
  }
  CASE(PRINT_B) {
    if (RA.i)
      print_line("True", 4);
    else
      print_line("False", 5);
    NEXT;
  }
  CASE(PRINT_S) {
    const TvmString *s = (const TvmString *)RA.p;
    if (s)
      print_line(STR_DATA(s), (size_t)s->len);
    else
      print_line("", 0);
    NEXT;
  }

#if !TVM_COMPUTED_GOTO
    default:
      FAULT("bad opcode");
    }
  }
#endif
}

TvmStatus tvm_run(TvmVm *vm, const TvmProto *proto, const TvmSlot *args, TvmSlot *rets) {
  return run(vm, proto, NULL, args, rets);
}

TvmStatus tvm_call_closure(TvmVm *vm, const TvmClosure *c, const TvmSlot *args, TvmSlot *rets) {
  if (vm->depth >= TVM_MAX_DEPTH) {
    tvm_set_error(vm, "fault: stack overflow (nested calls from the runtime)");
    vm->faulting = true;
    return TVM_FAULT;
  }
  vm->depth++;
  TvmStatus st = run(vm, c->proto, c, args, rets);
  vm->depth--;
  return st;
}
