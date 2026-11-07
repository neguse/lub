/* host の関数と、host が VM の object を読み書きする API (tcsvm.h、doc/vm-design.md §8.5、§13)。 */
#include "tcsvm_internal.h"

#include <stdio.h>
#include <stdlib.h>
#include <string.h>

static char *dup(const char *s) {
  size_t n = strlen(s) + 1;
  char *p = (char *)malloc(n);
  if (p)
    memcpy(p, s, n);
  return p;
}

/* 種類の列の 1 項 (tcsvm.h): 前に & (out) を付けてよい i f b s o か S{型の名前}。読めたら次の位置、読めなければ NULL */
static const char *host_token(const char *p, char *kind, bool *byref, const char **name, size_t *len) {
  *byref = *p == '&';
  if (*byref)
    p++;
  *name = NULL;
  *len = 0;
  if (*p && strchr("ifbso", *p)) {
    *kind = *p;
    return p + 1;
  }
  if (p[0] != 'S' || p[1] != '{')
    return NULL;
  const char *end = strchr(p + 2, '}');
  if (!end || end == p + 2)
    return NULL;
  *kind = 'S';
  *name = p + 2;
  *len = (size_t)(end - (p + 2));
  return end + 1;
}

int tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets, TvmHostFn fn, void *user) {
  /* 読み込んだ後に足すと、module が持つ host の関数の pointer がずれる */
  if (vm->nfuncs || !name || !params || !rets || !fn)
    return 0;
  char kind;
  bool byref;
  const char *tname;
  size_t tlen;
  for (const char *p = params; *p;)
    if (!(p = host_token(p, &kind, &byref, &tname, &tlen)))
      return 0;
  /* 戻り値は 1 つだけで、& は付かない (out は & の引数) */
  if (*rets) {
    const char *p = host_token(rets, &kind, &byref, &tname, &tlen);
    if (!p || byref || *p)
      return 0;
  }
  if (vm->nhosts == vm->caphosts) {
    uint32_t cap = vm->caphosts ? vm->caphosts * 2 : 32;
    TvmIntrinsic *h = (TvmIntrinsic *)realloc(vm->hosts, cap * sizeof(TvmIntrinsic));
    if (!h)
      return 0;
    vm->hosts = h;
    vm->caphosts = cap;
  }
  TvmIntrinsic *h = &vm->hosts[vm->nhosts];
  memset(h, 0, sizeof *h);
  h->name = dup(name);
  h->params = dup(params);
  h->rets = dup(rets);
  h->host = fn;
  h->user = user;
  if (!h->name || !h->params || !h->rets) {
    free((char *)h->name);
    free((char *)h->params);
    free((char *)h->rets);
    return 0;
  }
  vm->nhosts++;
  return 1;
}

TvmStatus tvm_host_fault(TvmVm *vm, const char *message) { return tvm_fault_msg(vm, message); }

/* ---- struct と out の写し (doc/vm-design.md §8.5) ----
   register の葉を、型の表の layout (doc/object-model.md §5) の C の struct の塊に写す。
   塊にする struct の葉は int / float / bool だけ (参照は塊の中で GC から見えない)。 */

/* S{name} の型: 型の表の struct で、葉に参照が無いもの */
static const TvmType *plan_struct(TvmVm *vm, const TvmIntrinsic *in, const char *name, size_t len) {
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    const TvmType *t = &vm->types[i];
    if (strlen(t->name) != len || memcmp(t->name, name, len) != 0)
      continue;
    if (t->tkind != TVM_T_STRUCT) {
      tvm_set_error(vm, "host %s: %s is not a struct", in->name, t->name);
      return NULL;
    }
    for (uint32_t k = 0; k < t->nleaves; k++)
      if (t->leaves[k].kind == TVM_K_REF) {
        tvm_set_error(vm,
                      "host %s: struct %s has a reference field (only int, float, bool, enum and structs of them cross "
                      "as a block)",
                      in->name, t->name);
        return NULL;
      }
    return t;
  }
  tvm_set_error(vm, "host %s: no struct type %.*s in the module", in->name, (int)len, name);
  return NULL;
}

/* *p の 1 項を解いて進める。葉の register は値なら *in_at、out なら *out_at から、struct の塊は *block から (どれも進める) */
static bool plan_arg(TvmVm *vm, const TvmIntrinsic *in, const char **p, TvmHostArg *x, uint32_t *in_at,
                     uint32_t *out_at, uint32_t *block) {
  const char *name;
  size_t len;
  *p = host_token(*p, &x->kind, &x->byref, &name, &len);
  uint32_t *at = x->byref ? out_at : in_at;
  x->st = NULL;
  x->reg = *at;
  x->block = 0;
  if (x->kind != 'S') {
    *at += 1;
    return true;
  }
  if (!(x->st = plan_struct(vm, in, name, len)))
    return false;
  *at += x->st->nleaves;
  x->block = *block;
  *block += (x->st->size + 7u) / 8u * 8u;
  return true;
}

void tvm_host_plans_free(TvmHostPlan **plans, uint32_t n) {
  if (!plans)
    return;
  for (uint32_t i = 0; i < n; i++)
    if (plans[i]) {
      free(plans[i]->args);
      free(plans[i]);
    }
  free(plans);
}

bool tvm_host_plans(TvmVm *vm) {
  vm->host_plans = (TvmHostPlan **)calloc(vm->nintrinsics ? vm->nintrinsics : 1, sizeof(TvmHostPlan *));
  if (!vm->host_plans) {
    tvm_set_error(vm, "out of memory");
    return false;
  }
  for (uint32_t i = 0; i < vm->nintrinsics; i++) {
    const TvmIntrinsic *in = vm->intrinsics[i];
    /* 葉の種類の文字だけの関数は register をそのまま渡す (S と & の無い列) */
    if (!in->host || !(strchr(in->params, 'S') || strchr(in->params, '&') || strchr(in->rets, 'S')))
      continue;
    TvmHostPlan *plan = (TvmHostPlan *)calloc(1, sizeof(TvmHostPlan));
    uint32_t n = 0;
    char kind;
    bool byref;
    const char *name;
    size_t len;
    for (const char *p = in->params; *p; n++)
      p = host_token(p, &kind, &byref, &name, &len);
    if (plan)
      plan->args = (TvmHostArg *)calloc(n ? n : 1, sizeof(TvmHostArg));
    if (!plan || !plan->args) {
      free(plan);
      tvm_set_error(vm, "out of memory");
      return false;
    }
    vm->host_plans[i] = plan;
    plan->nargs = n;
    /* 結果の register: 戻り値の葉、続けて out の葉 (C# の宣言の順)。値の引数の register は a から */
    uint32_t in_at = 0, out_at = 0, block = 0;
    const char *r = in->rets;
    if (*r && !plan_arg(vm, in, &r, &plan->ret, &out_at, &out_at, &block))
      return false;
    const char *p = in->params;
    for (uint32_t k = 0; k < n; k++)
      if (!plan_arg(vm, in, &p, &plan->args[k], &in_at, &out_at, &block))
        return false;
    plan->nslots = n + 1 + block / 8u;
  }
  return true;
}

static void pack(const TvmType *st, const TvmSlot *r, char *block) {
  for (uint32_t k = 0; k < st->nleaves; k++) {
    char *q = block + st->leaves[k].offset;
    switch (st->leaves[k].kind) {
    case TVM_K_I32:
      memcpy(q, &r[k].i, 4);
      break;
    case TVM_K_F32:
      memcpy(q, &r[k].f, 4);
      break;
    default: /* bool は 1 byte */
      *(uint8_t *)q = r[k].i != 0;
      break;
    }
  }
}

static void unpack(const TvmType *st, const char *block, TvmSlot *r) {
  for (uint32_t k = 0; k < st->nleaves; k++) {
    const char *q = block + st->leaves[k].offset;
    r[k].u = 0;
    switch (st->leaves[k].kind) {
    case TVM_K_I32:
      memcpy(&r[k].i, q, 4);
      break;
    case TVM_K_F32:
      memcpy(&r[k].f, q, 4);
      break;
    default:
      r[k].i = *(const uint8_t *)q != 0;
      break;
    }
  }
}

TvmStatus tvm_host_call(TvmVm *vm, const TvmIntrinsic *in, const TvmHostPlan *plan, TvmSlot *ra, TvmSlot *rb) {
  /* 呼び出しの間の領域は VM の stack の今の frame の後ろ (CALLR の SAVE が vm->top に置いた所)。
     host が tvm_invoke で VM に戻ると、入れ子の frame はこの領域の後ろから積むので、入れ子の host の関数の塊とは重ならない。 */
  TvmSlot *base = vm->top;
  if ((size_t)(vm->stack_end - base) < plan->nslots)
    return tvm_fault_msg(vm, "stack overflow");
  TvmSlot *args = base, *ret = base + plan->nargs;
  char *blocks = (char *)(ret + 1);
  memset(base, 0, sizeof(TvmSlot) * plan->nslots);
  for (uint32_t k = 0; k < plan->nargs; k++) {
    const TvmHostArg *x = &plan->args[k];
    TvmSlot *r = (x->byref ? rb : ra) + x->reg;
    if (x->st) {
      /* out の struct は 0 の塊、値の struct は葉を写した塊 */
      if (!x->byref)
        pack(x->st, r, blocks + x->block);
      args[k].p = blocks + x->block;
    } else if (x->byref) {
      /* out の scalar は結果の register そのもの (int32_t / float / bool / 参照は slot の先頭)。
         参照は frame の register なので、host が書いた後に VM に戻っても GC の root になる。 */
      r->u = 0;
      args[k].p = r;
    } else {
      args[k] = *r;
    }
  }
  TvmSlot *rets = rb;
  if (plan->ret.st) {
    ret->p = blocks + plan->ret.block;
    rets = ret;
  }
  vm->top = base + plan->nslots;
  TvmStatus st = in->host(vm, args, rets, in->user);
  vm->top = base;
  if (st != TVM_OK)
    return st;
  /* 塊を結果の register に戻す。bool の out は 0 / 1 に揃える */
  if (plan->ret.st)
    unpack(plan->ret.st, blocks + plan->ret.block, rb);
  for (uint32_t k = 0; k < plan->nargs; k++) {
    const TvmHostArg *x = &plan->args[k];
    if (!x->byref)
      continue;
    if (x->st)
      unpack(x->st, blocks + x->block, rb + x->reg);
    else if (x->kind == 'b')
      rb[x->reg].i = *(const uint8_t *)&rb[x->reg] != 0;
  }
  return TVM_OK;
}

static const TvmType *find_type(TvmVm *vm, const char *name) {
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (strcmp(vm->types[i].name, name) == 0)
      return &vm->types[i];
  return NULL;
}

/* ---- C の module の型 (doc/object-model.md §10.6) ---- */

/* VM の型の名前と C の module の型の名前は、namespace の区切り ('.' と '_') の違いを同じと見る */
static bool same_type_name(const char *a, const char *b) {
  for (; *a && *b; a++, b++)
    if (*a != *b && !((*a == '.' || *a == '_') && (*b == '.' || *b == '_')))
      return false;
  return *a == *b;
}

static int add_host_type(TvmVm *vm, const char *name, uint32_t type_id, TvmIfaceCheck check) {
  struct TvmHostType *h =
      (struct TvmHostType *)realloc(vm->host_types, (vm->nhost_types + 1) * sizeof(struct TvmHostType));
  if (!h)
    return 0;
  vm->host_types = h;
  char *copy = dup(name);
  if (!copy)
    return 0;
  vm->host_types[vm->nhost_types++] = (struct TvmHostType){copy, type_id, check};
  return tvm_bind_host_types(vm) ? 1 : 0;
}

int tvm_host_type(TvmVm *vm, const char *name, uint32_t type_id) {
  if (type_id >= TVM_TYPE_ID_VM)
    return 0;
  return add_host_type(vm, name, type_id, NULL);
}

int tvm_host_iface(TvmVm *vm, const char *name, TvmIfaceCheck check) {
  return check ? add_host_type(vm, name, 0, check) : 0;
}

bool tvm_bind_host_types(TvmVm *vm) {
  if (!vm->type_ids)
    return true; /* 型はまだ読んでいない (読み込みが当てる) */
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    const TvmType *t = &vm->types[i];
    vm->type_ids[i] = TVM_TYPE_ID_VM + i;
    vm->iface_checks[i] = NULL;
    for (uint32_t k = 0; k < vm->nhost_types; k++) {
      const struct TvmHostType *h = &vm->host_types[k];
      if (!same_type_name(t->name, h->name))
        continue;
      if (!h->check && t->tkind == TVM_T_CLASS)
        vm->type_ids[i] = h->type_id;
      else if (h->check && t->tkind == TVM_T_OBJECT)
        vm->iface_checks[i] = h->check;
    }
  }
  return true;
}

bool tvm_implements(TvmVm *vm, const void *object, const char *iface) {
  if (!object || GC_HEADER(object)->kind != TVM_KIND_OBJECT)
    return false;
  const TvmType *t = TVM_TYPE((const TvmObject *)object);
  if (t->tkind != TVM_T_CLASS)
    return false;
  for (uint32_t k = 0; k < t->nifaces; k++)
    if (same_type_name(vm->types[t->ifaces[k]].name, iface))
      return true;
  return false;
}

int32_t tvm_field_offset(TvmVm *vm, const char *type, const char *field) {
  const TvmType *t = find_type(vm, type);
  if (!t || (t->tkind != TVM_T_CLASS && t->tkind != TVM_T_STRUCT))
    return -1;
  for (uint32_t k = 0; k < t->nfields; k++)
    if (strcmp(t->fields[k].name, field) == 0)
      return (int32_t)t->fields[k].offset;
  return -1;
}

void *tvm_object_new(TvmVm *vm, const char *type) {
  const TvmType *t = find_type(vm, type);
  if (!t || t->tkind != TVM_T_CLASS)
    return NULL;
  TvmObject *o = (TvmObject *)tvm_alloc(vm, t->size);
  if (o)
    tvm_class_init(vm, o, t);
  return o;
}

const char *tvm_object_type(const void *object) {
  if (!object || GC_HEADER(object)->kind != TVM_KIND_OBJECT)
    return NULL; /* string と C の module の object */
  return TVM_TYPE(((const TvmObject *)object))->name;
}

const TvmTypeInfo *tvm_type(TvmVm *vm, const char *name) { return find_type(vm, name); }

uint32_t tvm_types_version(TvmVm *vm) { return vm->types_version; }

/* 型の表の版は、VM をまたいで同じ値を二度配らない。
   host は版で型の cache を決めるので、VM を作り直したとき前の VM と同じ版になると、捨てた VM の型を使い続ける */
uint32_t tvm_next_types_version(void) {
  static uint32_t last;
  return ++last;
}

int32_t tvm_type_field_offset(const TvmTypeInfo *t, const char *field) {
  if (!t || (t->tkind != TVM_T_CLASS && t->tkind != TVM_T_STRUCT))
    return -1;
  for (uint32_t k = 0; k < t->nfields; k++)
    if (strcmp(t->fields[k].name, field) == 0)
      return (int32_t)t->fields[k].offset;
  return -1;
}

void *tvm_object_new_of(TvmVm *vm, const TvmTypeInfo *t) {
  if (!t || t->tkind != TVM_T_CLASS)
    return NULL;
  TvmObject *o = (TvmObject *)tvm_alloc(vm, t->size);
  if (o)
    tvm_class_init(vm, o, t);
  return o;
}

void *tvm_list_new_of(TvmVm *vm, const TvmTypeInfo *t, int32_t count) {
  if (!t || t->tkind != TVM_T_LIST || count < 0)
    return NULL;
  TvmList *l = tvm_list_new(vm, t);
  if (l && count && !tvm_list_resize(vm, l, count))
    return NULL;
  return l;
}

bool tvm_list_resize(TvmVm *vm, void *list, int32_t count) {
  TvmList *l = (TvmList *)list;
  if (count < 0)
    return false;
  size_t es = TVM_TYPE(l)->elem_size;
  if (count > l->cap) {
    if ((uint64_t)count * es > ((uint64_t)1 << 31))
      return false;
    char *data = (char *)tvm_alloc_blob(vm, (size_t)count * es);
    if (!data)
      return false;
    if (l->count)
      memcpy(data, l->data, (size_t)l->count * es);
    l->data = data;
    l->cap = count;
    if (vm->heap)
      vm->heap->barrier(l);
  } else if (count > l->count) {
    memset(l->data + (size_t)l->count * es, 0, (size_t)(count - l->count) * es);
  } else if (count < l->count) {
    memset(l->data + (size_t)count * es, 0, (size_t)(l->count - count) * es); /* 外した参照を GC に見せない */
  }
  l->count = count;
  return true;
}

int32_t tvm_list_count(const void *list) { return list ? ((const TvmList *)list)->count : 0; }

void *tvm_list_data(const void *list) { return list ? ((const TvmList *)list)->data : NULL; }

void *tvm_array_new_of(TvmVm *vm, const TvmTypeInfo *t, int32_t len) {
  if (!t || t->tkind != TVM_T_ARRAY || len < 0)
    return NULL;
  if ((uint64_t)len * t->elem_size > ((uint64_t)1 << 31))
    return NULL;
  TvmArray *a = (TvmArray *)tvm_alloc(vm, sizeof(TvmArray) + (size_t)len * t->elem_size);
  if (!a)
    return NULL;
  TVM_TYPE(a) = t;
  a->len = len;
  return a;
}

void *tvm_blob_new(TvmVm *vm, size_t size) { return tvm_alloc_blob(vm, size ? size : 1); }

int32_t tvm_array_len(const void *array) { return array ? ((const TvmArray *)array)->len : 0; }

void *tvm_array_data(const void *array) { return array ? ARR_DATA((TvmArray *)array) : NULL; }

void *tvm_string_new(TvmVm *vm, const char *data, size_t len) { return tvm_str_new(vm, data, len); }

const char *tvm_string_data(const void *string, size_t *len) {
  const TvmString *s = (const TvmString *)string;
  if (!s) {
    *len = 0;
    return NULL;
  }
  *len = (size_t)s->len;
  return STR_DATA(s);
}

TvmHandle tvm_hold(TvmVm *vm, void *object) {
  if (!object)
    return 0;
  uint32_t i = 0;
  while (i < vm->nhandles && vm->handles[i])
    i++;
  if (i == vm->nhandles) {
    if (vm->nhandles == vm->caphandles) {
      uint32_t cap = vm->caphandles ? vm->caphandles * 2 : 16;
      void **h = (void **)realloc(vm->handles, cap * sizeof(void *));
      if (!h)
        return 0;
      vm->handles = h;
      vm->caphandles = cap;
    }
    vm->nhandles++;
  }
  vm->handles[i] = object;
  return i + 1;
}

void *tvm_handle_get(TvmVm *vm, TvmHandle h) { return h && h <= vm->nhandles ? vm->handles[h - 1] : NULL; }

void tvm_release(TvmVm *vm, TvmHandle h) {
  if (h && h <= vm->nhandles)
    vm->handles[h - 1] = NULL;
}

TvmStatus tvm_invoke(TvmVm *vm, void *closure, const TvmSlot *args, size_t nargs, TvmSlot *rets, size_t nrets) {
  const TvmClosure *c = (const TvmClosure *)closure;
  if (!c || TVM_TYPE(c)->tkind != TVM_T_FUNC) {
    tvm_set_error(vm, "tvm_invoke: not a delegate");
    return TVM_LOAD_ERROR;
  }
  if (nargs != c->proto->nparams || nrets < c->proto->nret) {
    tvm_set_error(vm, "tvm_invoke: %s takes %u argument slots and returns %u", c->proto->name, c->proto->nparams,
                  c->proto->nret);
    return TVM_LOAD_ERROR;
  }
  /* 外 (host の frame) から呼ぶときは tvm_call と同じく回収を止める。VM の呼び出しの中なら入れ子 */
  bool outer = vm->ftop == vm->frames && !vm->depth;
  if (outer) {
    vm->faulting = false;
    if (vm->heap)
      vm->heap->enter();
  }
  TvmStatus st = tvm_call_closure(vm, c, args, rets);
  if (outer && vm->heap)
    vm->heap->leave();
  return st;
}

TvmStatus tvm_invoke_method(TvmVm *vm, void *object, const char *method, const TvmSlot *args, size_t nargs,
                            TvmSlot *rets, size_t nrets) {
  const TvmType *t = object ? tvm_type_of(vm, object) : NULL;
  if (!t || t->tkind != TVM_T_CLASS) {
    tvm_set_error(vm, "tvm_invoke_method: not a VM object");
    return TVM_LOAD_ERROR;
  }
  const TvmProto *f = NULL;
  for (uint32_t k = 0; k < t->nitable && !f; k++) {
    const TvmSelector *sel = &vm->selectors[t->itable[k].selector];
    const TvmProto *g = &vm->funcs[t->itable[k].func];
    if (strcmp(sel->name, method) == 0 && g->nparams == nargs + 1 && g->nret <= nrets)
      f = g;
  }
  if (!f) {
    /* --ref の module の interface は VM の itable に無いので、型の関数を名前で引く */
    char name[512];
    snprintf(name, sizeof name, "%s.%s", t->name, method);
    int32_t i = tvm_find(vm, name);
    if (i >= 0 && vm->funcs[i].nparams == nargs + 1 && vm->funcs[i].nret <= nrets)
      f = &vm->funcs[i];
  }
  if (!f) {
    tvm_set_error(vm, "tvm_invoke_method: %s has no method %s with %zu argument slots", t->name, method, nargs);
    return TVM_LOAD_ERROR;
  }
  TvmSlot all[64];
  if (nargs + 1 > 64) {
    tvm_set_error(vm, "tvm_invoke_method: too many arguments");
    return TVM_LOAD_ERROR;
  }
  all[0].p = object;
  for (size_t k = 0; k < nargs; k++)
    all[k + 1] = args[k];
  bool outer = vm->ftop == vm->frames && !vm->depth;
  if (outer) {
    vm->faulting = false;
    if (vm->heap)
      vm->heap->enter();
  }
  TvmStatus st = tvm_run_nested(vm, f, all, rets);
  if (outer && vm->heap)
    vm->heap->leave();
  return st;
}

int tvm_host_delegate(TvmVm *vm, const char *sig, TvmDelegateFn fn) {
  if (vm->ndelegates == 32 || strlen(sig) >= sizeof vm->delegates[0].sig)
    return 0;
  snprintf(vm->delegates[vm->ndelegates].sig, sizeof vm->delegates[0].sig, "%s", sig);
  vm->delegates[vm->ndelegates++].fn = fn;
  return 1;
}

char tvm_sig_letter(const TvmVm *vm, uint8_t kind, uint16_t type) {
  switch (kind) {
  case TVM_K_I32:
    return 'i';
  case TVM_K_F32:
    return 'f';
  case TVM_K_BOOL:
    return 'b';
  case TVM_K_REF:
    return type == vm->string_type ? 's' : 'o';
  default:
    return '?';
  }
}

static char sig_letter(TvmVm *vm, const TvmRegInfo *info) { return tvm_sig_letter(vm, info->kind, info->type); }

static TvmDelegateFn delegate_by_sig(TvmVm *vm, const char *sig) {
  for (uint32_t i = 0; i < vm->ndelegates; i++)
    if (strcmp(vm->delegates[i].sig, sig) == 0)
      return vm->delegates[i].fn;
  return NULL;
}

TvmStatus tvm_call_delegate(TvmVm *vm, const TvmClosure *c, const char *sig, const TvmSlot *args, TvmSlot *rets) {
  if (!vm->heap || GC_HEADER(c)->kind == TVM_KIND_OBJECT)
    return tvm_call_closure(vm, c, args, rets);
  /* C の module の delegate (X-4) */
  TvmDelegateFn inv = delegate_by_sig(vm, sig);
  if (!inv) {
    tvm_set_error(vm, "fault: no invoke for a C delegate of type %s", sig);
    vm->faulting = true;
    return TVM_FAULT;
  }
  return inv((void *)c, args, rets);
}

TvmDelegateFn tvm_delegate_for(TvmVm *vm, const TvmType *t) {
  char sig[32];
  size_t n = 0;
  if (t->nparams + t->nrets + 2 > sizeof sig)
    return NULL;
  for (uint32_t k = 0; k < t->nparams; k++)
    sig[n++] = sig_letter(vm, &t->params[k]);
  sig[n++] = ':';
  for (uint32_t k = 0; k < t->nrets; k++)
    sig[n++] = sig_letter(vm, &t->rets[k]);
  sig[n] = '\0';
  return delegate_by_sig(vm, sig);
}
