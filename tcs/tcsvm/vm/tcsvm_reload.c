/* hot reload の適用 (doc/vm-design.md §12、doc/object-model.md §6、§7)。
   新版の module を別の VM に読み、計画 (tcs2vm --reload-from が作る。tcs2c の migrator と同じ形) に従って
   heap と static を新版の型に移し、関数表を差し替える。

   段:
   1. 読み込み: 新版を別の VM で parse / verify / link する。失敗は TVM_LOAD_ERROR (状態は旧版のまま)
   2. 計画: 型を名前で対応させ、生きている object の型と closure の関数が新版にあるかを確かめる。
      計画の restart の理由と、対応の付かない object は TVM_RESTART (状態は旧版のまま)
   3. 反映: layout の変わる object を新しい layout で作り直して (共有の tcs_migrate_value) 旧 object に転送先を書き、
      ほかの object は型を新版に付け替え、static を名前で移し、module を入れ替え、全部の参照を共有の walker で
      転送先へ張り替える。ここに失敗の経路は無い (確保の失敗は abort)
   4. initializer: 足した (と型を変えた) field の initializer を、作り直した instance ごとに走らせる。
      足した static の initializer (`型.<reload-static>`) を走らせる
   5. hook: OnReload を持つ型の生きている instance ごとに OnReload()、続いて entry の型の static の OnReload()

   initializer は新版の関数なので、反映の後 (型と heap が新版で揃ってから) に走らせる。fault は hook と同じく
   警告にし (field は 0 の値のまま)、反映は巻き戻さない (doc/object-model.md §6)。 */
#include "../../runtime/model/tcs_walk.h"
#include "tcsvm_internal.h"

#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define NONE 0xFFFFFFFFu

typedef struct Reload {
  TvmVm *vm; /* 旧版 (反映の後は新版) */
  TvmVm *nv; /* 新版を読んだ VM (反映の後は旧版の module を持ち、解放する) */
  TcsPlan plan;
  char **plan_strings;
  uint32_t nplan_strings;
  uint32_t *type_map; /* 旧版の型の番号 → 新版の型の番号 (NONE は無し) */
  uint8_t
      *rebuild; /* 旧版の型ごと: object (か要素) を新しい layout で作り直す */
  uint32_t
      *proto_map; /* 旧版の関数 → 新版の関数 (NONE は未定、NONE - 1 は無し) */
  char warnings[sizeof(((TvmVm *)0)->error)];
} Reload;

static void *xalloc(void *p) {
  if (!p)
    abort(); /* 反映の段には失敗の経路を作らない (doc/vm-design.md §12.1) */
  return p;
}

static void warn(Reload *rl, const char *fmt, ...) {
  size_t used = strlen(rl->warnings);
  if (used + 2 >= sizeof rl->warnings)
    return;
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(rl->warnings + used, sizeof rl->warnings - used, fmt, ap);
  va_end(ap);
}

static uint32_t find_type(const TvmVm *vm, const char *name) {
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (strcmp(vm->types[i].name, name) == 0)
      return i;
  return NONE;
}

static const char *type_name(const TvmVm *vm, uint16_t t) {
  return t == TVM_NO_TYPE || t >= vm->ntypes ? "" : vm->types[t].name;
}

/* 旧版と新版の (種類、型) が同じ値か (型は名前で比べる) */
static bool same_info(const TvmVm *a, TvmRegInfo x, const TvmVm *b,
                      TvmRegInfo y) {
  return x.kind == y.kind &&
         strcmp(type_name(a, x.type), type_name(b, y.type)) == 0;
}

static bool relayout(const Reload *rl, const char *name) {
  const TcsPlanType *pt = tcs_plan_type(&rl->plan, name);
  return pt && pt->change == TCS_PLAN_RELAYOUT;
}

/* ---- 2. 計画 ---- */

/* 型の対応と、作り直すかどうか。計画が SAME と言う型の layout が実際に違えば、計画と module の食い違い */
static const char *plan_types(Reload *rl) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  rl->type_map =
      (uint32_t *)xalloc(calloc(vm->ntypes ? vm->ntypes : 1, sizeof(uint32_t)));
  rl->rebuild = (uint8_t *)xalloc(calloc(vm->ntypes ? vm->ntypes : 1, 1));
  for (uint32_t i = 0; i < vm->ntypes; i++) {
    const TvmType *ot = &vm->types[i];
    uint32_t j = find_type(nv, ot->name);
    rl->type_map[i] = j;
    if (j == NONE)
      continue;
    const TvmType *nt = &nv->types[j];
    if (nt->tkind != ot->tkind) {
      rl->type_map[i] = NONE;
      continue;
    }
    switch (ot->tkind) {
    case TVM_T_CLASS:
    case TVM_T_STRUCT:
      rl->rebuild[i] = relayout(rl, ot->name);
      if (!rl->rebuild[i] &&
          (ot->size != nt->size || ot->nleaves != nt->nleaves))
        return ot->name;
      for (uint32_t k = 0; !rl->rebuild[i] && k < ot->nleaves; k++)
        if (ot->leaves[k].offset != nt->leaves[k].offset ||
            !same_info(vm, (TvmRegInfo){ot->leaves[k].kind, ot->leaves[k].type},
                       nv,
                       (TvmRegInfo){nt->leaves[k].kind, nt->leaves[k].type}))
          return ot->name;
      break;
    case TVM_T_ARRAY:
    case TVM_T_LIST:
    case TVM_T_DICT:
      rl->rebuild[i] = ot->elem_kind == TVM_K_STRUCT &&
                       relayout(rl, type_name(vm, ot->elem_type));
      if (!rl->rebuild[i] && ot->elem_size != nt->elem_size)
        return ot->name;
      break;
    default:
      break;
    }
  }
  return NULL;
}

static bool same_shape(const TvmVm *vm, const TvmProto *op, const TvmVm *nv,
                       const TvmProto *np) {
  bool ok = np->nparams == op->nparams && np->nret == op->nret &&
            np->ncaps == op->ncaps;
  for (uint32_t k = 0; ok && k < op->nparams; k++)
    ok = same_info(vm, op->regs[k], nv, np->regs[k]);
  for (uint32_t k = 0; ok && k < op->nret; k++)
    ok = same_info(vm, op->rets[k], nv, np->rets[k]);
  for (uint32_t k = 0; ok && k < op->ncaps; k++)
    ok = same_info(vm, op->caps[k], nv, np->caps[k]);
  return ok;
}

/* lambda の名前 `外側<lambdaN:指紋>` を外側 (長さ)、番号、指紋に分ける。lambda でなければ false */
static bool lambda_parts(const char *name, size_t *outer, long *ordinal,
                         const char **print) {
  const char *at = NULL;
  for (const char *p = strstr(name, "<lambda"); p; p = strstr(p + 1, "<lambda"))
    at = p;
  if (!at)
    return false;
  char *end;
  *outer = (size_t)(at - name);
  *ordinal = strtol(at + 7, &end, 10);
  if (*end != ':')
    return false;
  *print = end + 1;
  return true;
}

static uint32_t count_lambdas(const TvmVm *vm, const char *name, size_t outer) {
  uint32_t n = 0;
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    size_t o;
    long k;
    const char *p;
    if (lambda_parts(vm->funcs[i].name, &o, &k, &p) && o == outer &&
        strncmp(vm->funcs[i].name, name, outer) == 0)
      n++;
  }
  return n;
}

/* closure の関数の新版: 引数・戻り値・捕捉の (種類、型) が同じ関数で、
   1. 同じ名前 (関数、または外側・番号・本文が同じ lambda)
   2. lambda なら、同じ外側の関数の中で本文の指紋が同じ lambda (前に lambda を足しても番号に依らず対応する)
   3. lambda で本文を変えたなら、外側の関数の lambda の数が変わらないときだけ同じ番号の lambda */
static uint32_t map_proto(Reload *rl, const TvmProto *op) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  uint32_t oi = (uint32_t)(op - vm->funcs);
  if (rl->proto_map[oi] != NONE)
    return rl->proto_map[oi];
  uint32_t found = NONE - 1;
  int32_t j = tvm_find(nv, op->name);
  size_t outer;
  long ordinal;
  const char *print;
  if (j >= 0 && same_shape(vm, op, nv, &nv->funcs[j]))
    found = (uint32_t)j;
  else if (lambda_parts(op->name, &outer, &ordinal, &print)) {
    uint32_t by_print = NONE, by_print_count = 0, by_ordinal = NONE;
    for (uint32_t i = 0; i < nv->nfuncs; i++) {
      size_t o;
      long k;
      const char *p;
      const char *name = nv->funcs[i].name;
      if (!lambda_parts(name, &o, &k, &p) || o != outer ||
          strncmp(name, op->name, outer) != 0 ||
          !same_shape(vm, op, nv, &nv->funcs[i]))
        continue;
      if (strcmp(p, print) == 0) {
        by_print = i;
        by_print_count++;
      }
      if (k == ordinal)
        by_ordinal = i;
    }
    if (by_print_count == 1)
      found = by_print;
    else if (by_print_count == 0 && by_ordinal != NONE &&
             count_lambdas(vm, op->name, outer) ==
                 count_lambdas(nv, op->name, outer))
      found = by_ordinal;
  }
  return rl->proto_map[oi] = found;
}

static TvmStatus restart(Reload *rl, const char *fmt, const char *what) {
  tvm_set_error(rl->vm, fmt, what);
  return TVM_RESTART;
}

/* 生きている VM の object (blob と定数を除く) の列。shared heap では frame の境界の回収の後の旧世代から
   (C の module の object は除く) */
typedef struct Objs {
  void **p;
  size_t n, cap;
} Objs;

static void objs_add(Objs *o, void *p) {
  if (o->n == o->cap) {
    o->cap = o->cap ? o->cap * 2 : 256;
    o->p = (void **)xalloc(realloc(o->p, o->cap * sizeof(void *)));
  }
  o->p[o->n++] = p;
}

static void objs_each(void *payload, uint32_t kind, void *ctx) {
  if (kind == TVM_KIND_OBJECT &&
      !(GC_HEADER(payload)->flags & (TVM_GC_STATIC | TVM_GC_RELOADED)))
    objs_add((Objs *)ctx, payload);
}

static Objs snapshot(TvmVm *vm) {
  Objs o = {0};
  if (vm->heap)
    vm->heap->each(objs_each, &o);
  else
    for (TvmGcHeader *h = vm->objects; h; h = h->next)
      if (!GC_BLOB(h) && !(h->flags & (TVM_GC_STATIC | TVM_GC_RELOADED)))
        objs_add(&o, h + 1);
  return o;
}

/* 生きている object の型と closure の関数が新版にあること */
static TvmStatus plan_heap(Reload *rl) {
  TvmVm *vm = rl->vm;
  rl->proto_map = (uint32_t *)xalloc(
      malloc((vm->nfuncs ? vm->nfuncs : 1) * sizeof(uint32_t)));
  for (uint32_t i = 0; i < vm->nfuncs; i++)
    rl->proto_map[i] = NONE;
  Objs objs = snapshot(vm);
  for (size_t i = 0; i < objs.n; i++) {
    const TvmType *t = TVM_TYPE(objs.p[i]);
    if (rl->type_map[t - vm->types] == NONE) {
      free(objs.p);
      return restart(
          rl, "a live object has type %s, which the new version does not have",
          t->name);
    }
    if (t->tkind == TVM_T_FUNC) {
      const TvmClosure *c = (const TvmClosure *)objs.p[i];
      if (map_proto(rl, c->proto) == NONE - 1) {
        free(objs.p);
        return restart(rl,
                       "a live closure runs %s, which the new version does not "
                       "have with the same shape",
                       c->proto->name);
      }
    }
  }
  free(objs.p);
  if ((vm->nconsts || vm->nretired) && rl->nv->string_type == TVM_NO_TYPE)
    return restart(rl, "%s",
                   "the new version has no string type, but the old one has "
                   "string constants");
  return TVM_OK;
}

/* ---- 3. 反映 ---- */

static void forward(void *old, void *nw) {
  GC_HEADER(old)->flags |= TVM_GC_RELOADED;
  GC_HEADER(old)->type = (const TvmType *)nw; /* 旧 object の型はもう読まない */
  GC_HEADER(old)->kind =
      TVM_KIND_BLOB; /* GC が中を辿らない (shared heap では次の回収で消える) */
}

static void root(TvmVm *vm, void *p) {
  if (vm->nreload_roots == vm->capreload_roots) {
    size_t cap = vm->capreload_roots ? vm->capreload_roots * 2 : 64;
    vm->reload_roots =
        (void **)xalloc(realloc(vm->reload_roots, cap * sizeof(void *)));
    vm->capreload_roots = cap;
  }
  vm->reload_roots[vm->nreload_roots++] = p;
}

/* 要素 n 個を旧版の struct 型 ot から新版の nt へ (要素の大きさはそれぞれ) */
static void migrate_elems(Reload *rl, const TvmType *ot, const char *src,
                          uint32_t osize, const TvmType *nt, char *dst,
                          uint32_t nsize, uint32_t n) {
  for (uint32_t i = 0; i < n; i++)
    tcs_migrate_value(&rl->plan, rl->vm->types, ot, src + (size_t)i * osize,
                      rl->nv->types, nt, dst + (size_t)i * nsize);
}

/* object を新版の型に移す。作り直した class の instance は initializer のために root に積む */
static void migrate_object(Reload *rl, void *p) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  const TvmType *ot = TVM_TYPE(((TvmObject *)p));
  uint32_t oi = (uint32_t)(ot - vm->types);
  const TvmType *nt = &nv->types[rl->type_map[oi]];
  if (!rl->rebuild[oi]) {
    if (ot->tkind == TVM_T_CLASS)
      tvm_class_init(nv, (TvmObject *)p, nt);
    else
      TVM_TYPE(((TvmObject *)p)) = nt;
    if (ot->tkind == TVM_T_FUNC) {
      TvmClosure *c = (TvmClosure *)p;
      c->proto = &nv->funcs[rl->proto_map[c->proto - vm->funcs]];
    }
    return;
  }
  switch (ot->tkind) {
  case TVM_T_CLASS: {
    TvmObject *o = (TvmObject *)xalloc(tvm_alloc(vm, nt->size));
    tvm_class_init(nv, o, nt);
    tcs_migrate_value(&rl->plan, vm->types, ot, (const char *)p, nv->types, nt,
                      (char *)o);
    if ((ot->flags & TCS_TYPE_HOST_VALUE) && (nt->flags & TCS_TYPE_HOST_VALUE))
      TVM_HOST_VALUE(o) = TVM_HOST_VALUE(p);
    forward(p, o);
    root(vm, o);
    break;
  }
  case TVM_T_ARRAY: {
    TvmArray *a = (TvmArray *)p;
    TvmArray *b = (TvmArray *)xalloc(
        tvm_alloc(vm, sizeof(TvmArray) + (size_t)a->len * nt->elem_size));
    TVM_TYPE(b) = nt;
    b->len = a->len;
    migrate_elems(rl, &vm->types[ot->elem_type], ARR_DATA(a), ot->elem_size,
                  &nv->types[nt->elem_type], ARR_DATA(b), nt->elem_size,
                  (uint32_t)a->len);
    forward(p, b);
    root(vm, b);
    break;
  }
  case TVM_T_LIST: {
    /* List の object は同じまま、要素の buffer を新しい要素の大きさで確保し直す */
    TvmList *l = (TvmList *)p;
    if (l->data) {
      char *data =
          (char *)xalloc(tvm_alloc_blob(vm, (size_t)l->cap * nt->elem_size));
      migrate_elems(rl, &vm->types[ot->elem_type], l->data, ot->elem_size,
                    &nv->types[nt->elem_type], data, nt->elem_size,
                    (uint32_t)l->count);
      l->data = data;
    }
    TVM_TYPE(l) = nt;
    break;
  }
  case TVM_T_DICT: {
    /* entry の番号は変えない (bucket はそのまま使える) */
    TvmDict *d = (TvmDict *)p;
    if (d->entries) {
      uint32_t oe = ot->dict_entry, ne = nt->dict_entry;
      char *entries = (char *)xalloc(tvm_alloc_blob(vm, (size_t)d->cap * ne));
      for (int32_t i = 0; i < d->used; i++) {
        TvmDictEntry *from = (TvmDictEntry *)(d->entries + (size_t)i * oe);
        TvmDictEntry *to = (TvmDictEntry *)(entries + (size_t)i * ne);
        *to = *from;
        if (from->live)
          tcs_migrate_value(&rl->plan, vm->types, &vm->types[ot->elem_type],
                            DICT_VALUE(from), nv->types,
                            &nv->types[nt->elem_type], DICT_VALUE(to));
      }
      d->entries = entries;
    }
    TVM_TYPE(d) = nt;
    break;
  }
  default:
    TVM_TYPE(((TvmObject *)p)) = nt;
    break;
  }
}

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

static TvmSlot mem_to_slot(uint8_t kind, const char *p) {
  TvmSlot s = {0};
  switch (kind) {
  case TVM_K_I32:
    memcpy(&s.i, p, 4);
    break;
  case TVM_K_F32:
    memcpy(&s.f, p, 4);
    break;
  case TVM_K_BOOL:
    s.i = *(const uint8_t *)p;
    break;
  default:
    memcpy(&s.p, p, sizeof(void *));
    break;
  }
  return s;
}

/* static を名前で移す。struct の static は葉を値に組み立て、struct の計画で移してから葉に戻す */
static void migrate_statics(Reload *rl) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  for (uint32_t i = 0; i < nv->nstatic_fields; i++) {
    const TvmStaticField *nf = &nv->static_fields[i];
    const TvmStaticField *of = NULL;
    for (uint32_t k = 0; k < vm->nstatic_fields && !of; k++)
      if (strcmp(vm->static_fields[k].name, nf->name) == 0)
        of = &vm->static_fields[k];
    if (!of || !same_info(vm, (TvmRegInfo){of->kind, of->type}, nv,
                          (TvmRegInfo){nf->kind, nf->type}))
      continue; /* 足した static (initializer は後で) か、型の変わった static (0 の値) */
    if (nf->kind != TVM_K_STRUCT) {
      nv->statics[nf->slot] = vm->statics[of->slot];
      continue;
    }
    const TvmType *ot = &vm->types[of->type], *nt = &nv->types[nf->type];
    char *src = (char *)xalloc(calloc(1, ot->size ? ot->size : 1));
    char *dst = (char *)xalloc(calloc(1, nt->size ? nt->size : 1));
    for (uint32_t k = 0; k < ot->nleaves; k++)
      slot_to_mem(ot->leaves[k].kind, vm->statics[of->slot + k],
                  src + ot->leaves[k].offset);
    tcs_migrate_value(&rl->plan, vm->types, ot, src, nv->types, nt, dst);
    for (uint32_t k = 0; k < nt->nleaves; k++)
      nv->statics[nf->slot + k] =
          mem_to_slot(nt->leaves[k].kind, dst + nt->leaves[k].offset);
    free(src);
    free(dst);
  }
}

/* 状態の控え (tvm_snapshot_take) の static は slot で持つので、名前で新版の slot へ付け替える。
   初期化子の付いた static を足すと後ろの static の slot がずれるため。
   消えた static と型の変わった static、葉の並びの変わった struct の static は控えから落とす (書き戻さない) */
static void remap_snapshot_statics(Reload *rl) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  if (!vm->snapshots)
    return;
  uint32_t *to = (uint32_t *)xalloc(
      malloc((vm->nstatics ? vm->nstatics : 1) * sizeof(uint32_t)));
  for (uint32_t i = 0; i < vm->nstatics; i++)
    to[i] = UINT32_MAX;
  for (uint32_t i = 0; i < vm->nstatic_fields; i++) {
    const TvmStaticField *of = &vm->static_fields[i];
    const TvmStaticField *nf = NULL;
    for (uint32_t k = 0; k < nv->nstatic_fields && !nf; k++)
      if (strcmp(nv->static_fields[k].name, of->name) == 0)
        nf = &nv->static_fields[k];
    if (!nf || !same_info(vm, (TvmRegInfo){of->kind, of->type}, nv,
                          (TvmRegInfo){nf->kind, nf->type}))
      continue;
    if (of->kind != TVM_K_STRUCT) {
      to[of->slot] = nf->slot;
      continue;
    }
    const TvmType *ot = &vm->types[of->type], *nt = &nv->types[nf->type];
    bool same = ot->nleaves == nt->nleaves;
    for (uint32_t k = 0; k < ot->nleaves && same; k++)
      same = ot->leaves[k].kind == nt->leaves[k].kind &&
             ot->leaves[k].offset == nt->leaves[k].offset;
    for (uint32_t k = 0; k < ot->nleaves && same; k++)
      to[of->slot + k] = nf->slot + k;
  }
  for (TvmSnapshot *s = vm->snapshots; s; s = s->next) {
    uint32_t n = 0;
    for (uint32_t i = 0; i < s->nstatics; i++) {
      uint32_t slot = s->statics[i].slot;
      if (slot >= vm->nstatics || to[slot] == UINT32_MAX)
        continue;
      s->statics[n] = s->statics[i];
      s->statics[n].slot = to[slot];
      n++;
    }
    s->nstatics = n;
  }
  free(to);
}

/* module の部分 (strings、定数、型、static、関数) を 2 つの VM の間で入れ替える */
#define SWAP(type, field)                                                      \
  do {                                                                         \
    type tmp_ = a->field;                                                      \
    a->field = b->field;                                                       \
    b->field = tmp_;                                                           \
  } while (0)

static void swap_module(TvmVm *a, TvmVm *b) {
  SWAP(uint32_t, nstrings);
  SWAP(char **, strings);
  SWAP(uint32_t, nconsts);
  SWAP(TvmString **, consts);
  SWAP(uint32_t, ntypes);
  SWAP(TvmType *, types);
  SWAP(uint32_t *, type_ids);
  SWAP(TvmIfaceCheck *, iface_checks);
  SWAP(uint16_t, string_type);
  SWAP(uint32_t, nstatics);
  SWAP(TvmRegInfo *, static_info);
  SWAP(TvmSlot *, statics);
  SWAP(uint32_t, nstatic_fields);
  SWAP(TvmStaticField *, static_fields);
  SWAP(uint32_t, nfuncs);
  SWAP(TvmProto *, funcs);
  SWAP(int32_t, entry);
  SWAP(uint32_t, nselectors);
  SWAP(TvmSelector *, selectors);
  SWAP(uint32_t, nintrinsics);
  SWAP(const TvmIntrinsic **, intrinsics);
  SWAP(TvmHostPlan **, host_plans);
  SWAP(TvmDebug *, debug);
}

static void fix_slot(void *ctx, void **slot);
static void fix_slot_heap(void **slot) { fix_slot(NULL, slot); }

static void fix_slot(void *ctx, void **slot) {
  (void)ctx;
  if (*slot && (GC_HEADER(*slot)->flags & TVM_GC_RELOADED))
    *slot = (void *)GC_HEADER(*slot)->type;
}

static void reflect(Reload *rl) {
  TvmVm *vm = rl->vm, *nv = rl->nv;
  /* 移す前の object の列 (作り直した object は入らない) */
  Objs objs = snapshot(vm);
  for (size_t i = 0; i < objs.n; i++)
    migrate_object(rl, objs.p[i]);
  migrate_statics(rl);
  remap_snapshot_statics(rl);
  /* 旧版の string の定数は heap から指されうるので残し、型を新版の string にする */
  if (vm->nconsts) {
    TvmString **retired = (TvmString **)xalloc(realloc(
        vm->retired, (vm->nretired + vm->nconsts) * sizeof(TvmString *)));
    for (uint32_t i = 0; i < vm->nconsts; i++) {
      TVM_TYPE(vm->consts[i]) = &nv->types[nv->string_type];
      retired[vm->nretired++] = vm->consts[i];
      vm->consts[i] = NULL;
    }
    vm->retired = retired;
  }
  for (uint32_t i = 0; i < vm->nretired; i++)
    TVM_TYPE(vm->retired[i]) = &nv->types[nv->string_type];
  swap_module(vm, nv);
  vm->types_version = tvm_next_types_version();
  /* 作り直した object があれば、それより前の状態の控えは戻せない */
  for (uint32_t i = 0; i < objs.n; i++)
    if (GC_HEADER(objs.p[i])->flags & TVM_GC_RELOADED) {
      vm->layout_version++;
      tvm_snapshots_invalidate(vm);
      break;
    }
  /* 弱く覚えた object も転送先へ */
  for (uint32_t i = 0; i < vm->npinned; i++)
    if (vm->pinned[i].p &&
        (GC_HEADER(vm->pinned[i].p)->flags & TVM_GC_RELOADED))
      vm->pinned[i].p = (void *)GC_HEADER(vm->pinned[i].p)->type;
  /* 新版の host の関数は読み込みに使った nv の写しを指すので、同じ番号の vm の登録へ付け替える */
  for (uint32_t i = 0; i < vm->nintrinsics; i++)
    if (nv->nhosts && vm->intrinsics[i] >= nv->hosts &&
        vm->intrinsics[i] < nv->hosts + nv->nhosts)
      vm->intrinsics[i] = &vm->hosts[vm->intrinsics[i] - nv->hosts];
  /* 張り替え: 全部の object と static の参照を転送先へ (共有の walker、sink は fix_slot) */
  if (vm->heap) {
    /* shared heap: C の module の object と root も (X-3)。作り直した object は nursery にあるので別に辿り、
       旧 object が新しい object を指すようになったので barrier に載せる */
    vm->heap->visit_all(fix_slot_heap);
    for (size_t i = 0; i < vm->nreload_roots; i++)
      tvm_object_refs(vm, vm->reload_roots[i], fix_slot, NULL);
    for (size_t i = 0; i < objs.n; i++)
      if (!(GC_HEADER(objs.p[i])->flags & TVM_GC_RELOADED))
        vm->heap->barrier(objs.p[i]);
  } else
    for (TvmGcHeader *h = vm->objects; h; h = h->next)
      if (!GC_BLOB(h) && !(h->flags & TVM_GC_RELOADED))
        tvm_object_refs(vm, h + 1, fix_slot, NULL);
  free(objs.p);
  for (uint32_t i = 0; i < vm->nstatics; i++)
    if (vm->static_info[i].kind == TVM_K_REF)
      fix_slot(NULL, &vm->statics[i].p);
  /* host が持つ object (C の module の root) も同じ sink で張り替える */
  for (uint32_t i = 0; i < vm->nhandles; i++)
    fix_slot(NULL, &vm->handles[i]);
  /* 作り直した旧 object はどこからも指されないので、ここで解放する (shared heap では次の回収が消す) */
  TvmGcHeader **link = &vm->objects;
  while (!vm->heap && *link) {
    TvmGcHeader *h = *link;
    if (h->flags & TVM_GC_RELOADED) {
      *link = h->next;
      free(h);
    } else
      link = &h->next;
  }
}

/* ---- 4. initializer と 5. hook ---- */

static void call_warn(Reload *rl, int32_t func, TvmSlot *args, size_t nargs,
                      TvmSlot *rets, size_t nrets) {
  TvmVm *vm = rl->vm;
  if (tvm_call(vm, func, args, nargs, rets, nrets) != TVM_OK)
    warn(rl, "%s%s", rl->warnings[0] ? "\n" : "", vm->error);
}

static void run_initializers(Reload *rl) {
  TvmVm *vm = rl->vm;
  size_t n = vm->nreload_roots;
  for (size_t i = 0; i < n; i++) {
    TvmObject *o = (TvmObject *)vm->reload_roots[i];
    const TcsPlanType *pt = tcs_plan_type(&rl->plan, TVM_TYPE(o)->name);
    for (uint32_t k = 0; pt && k < pt->nfields && k < TVM_TYPE(o)->nfields;
         k++) {
      uint32_t init = pt->fields[k].init;
      if (pt->fields[k].source == TCS_FIELD_RETAINED || !init ||
          init > vm->nfuncs)
        continue;
      const TvmProto *f = &vm->funcs[init - 1];
      const TcsField *field = &TVM_TYPE(o)->fields[k];
      TvmSlot rets[256];
      if (f->nparams || f->nret > 256)
        continue;
      memset(rets, 0, sizeof rets);
      size_t before = strlen(rl->warnings);
      call_warn(rl, (int32_t)(init - 1), NULL, 0, rets, f->nret);
      if (strlen(rl->warnings) != before)
        continue; /* fault: 0 の値のまま */
      if (field->kind == TVM_K_STRUCT) {
        const TvmType *st = &vm->types[field->type];
        for (uint32_t j = 0; j < st->nleaves && j < f->nret; j++)
          slot_to_mem(st->leaves[j].kind, rets[j],
                      (char *)o + field->offset + st->leaves[j].offset);
      } else if (f->nret == 1)
        slot_to_mem(field->kind, rets[0], (char *)o + field->offset);
    }
  }
  /* 足した static: 遅延初期化の型で、まだ初期化していなければ最初の読み書きで全部を初期化するので走らせない */
  for (uint32_t i = 0; i < vm->nfuncs; i++) {
    const char *name = vm->funcs[i].name;
    const char *suffix = ".<reload-static>";
    size_t len = strlen(name), sl = strlen(suffix);
    if (len <= sl || strcmp(name + len - sl, suffix) != 0)
      continue;
    char flag[512];
    snprintf(flag, sizeof flag, "%.*s.<init>", (int)(len - sl), name);
    bool skip = false;
    for (uint32_t k = 0; k < vm->nstatic_fields; k++)
      if (strcmp(vm->static_fields[k].name, flag) == 0)
        skip = vm->statics[vm->static_fields[k].slot].i == 0;
    if (!skip)
      call_warn(rl, (int32_t)i, NULL, 0, NULL, 0);
  }
}

static void run_hooks(Reload *rl) {
  TvmVm *vm = rl->vm;
  /* hook が確保しても対象が変わらないように、先に全部の対象を root に積む */
  vm->nreload_roots = 0;
  if (vm->heap)
    vm->heap->frame(); /* 作り直した object も旧世代に置いてから列挙する */
  Objs objs = snapshot(vm);
  for (size_t i = 0; i < objs.n; i++) {
    const TvmType *t = TVM_TYPE(objs.p[i]);
    if (t->tkind == TVM_T_CLASS && (t->flags & TCS_TYPE_HAS_ON_RELOAD))
      root(vm, objs.p[i]);
  }
  free(objs.p);
  char name[512];
  for (size_t i = 0; i < vm->nreload_roots; i++) {
    TvmObject *o = (TvmObject *)vm->reload_roots[i];
    snprintf(name, sizeof name, "%s.OnReload", TVM_TYPE(o)->name);
    int32_t f = tvm_find(vm, name);
    if (f < 0 || vm->funcs[f].nparams != 1 || vm->funcs[f].nret != 0)
      continue;
    TvmSlot self = {.p = o};
    call_warn(rl, f, &self, 1, NULL, 0);
  }
  vm->nreload_roots = 0;
  /* entry の型の static の OnReload */
  if (vm->entry >= 0) {
    const char *entry = vm->funcs[vm->entry].name;
    const char *dot = strrchr(entry, '.');
    if (dot) {
      snprintf(name, sizeof name, "%.*s.OnReload", (int)(dot - entry), entry);
      int32_t f = tvm_find(vm, name);
      if (f >= 0 && vm->funcs[f].nparams == 0 && vm->funcs[f].nret == 0)
        call_warn(rl, f, NULL, 0, NULL, 0);
    }
  }
}

static void reload_free(Reload *rl) {
  tcs_plan_free(&rl->plan);
  if (rl->plan_strings)
    tcs_model_free_strings(rl->plan_strings, rl->nplan_strings);
  free(rl->type_map);
  free(rl->rebuild);
  free(rl->proto_map);
  tvm_free(rl->nv);
  free(rl);
}

TvmStatus tvm_apply(TvmVm *vm, const uint8_t *module, size_t size,
                    const uint8_t *plan, size_t plan_size) {
  vm->error[0] = '\0';
  if (!vm->nfuncs) {
    tvm_set_error(vm, "tvm_apply needs a loaded module");
    return TVM_LOAD_ERROR;
  }
  if (vm->ftop != vm->frames || vm->depth || vm->npins || vm->nstruct_pins) {
    tvm_set_error(vm, "tvm_apply runs only at a frame boundary");
    return TVM_LOAD_ERROR;
  }
  /* shared heap: 若い object を旧世代に置いてから辿る (nursery の object は列挙できない) */
  if (vm->heap)
    vm->heap->frame();
  Reload *rl = (Reload *)calloc(1, sizeof(Reload));
  if (!rl) {
    tvm_set_error(vm, "out of memory");
    return TVM_LOAD_ERROR;
  }
  rl->vm = vm;
  /* 1. 読み込み (新版は実行しないので stack は小さくてよい) */
  TvmConfig config = {16, 4, 0};
  rl->nv = tvm_new(&config);
  if (!rl->nv) {
    free(rl);
    tvm_set_error(vm, "out of memory");
    return TVM_LOAD_ERROR;
  }
  /* 新版の読み込みも同じ host の関数を引く。新しい VM に登録を写す (反映の後も intrinsics は旧版の vm->hosts を指す) */
  for (uint32_t i = 0; i < vm->nhosts; i++)
    tvm_host(rl->nv, vm->hosts[i].name, vm->hosts[i].params, vm->hosts[i].rets,
             vm->hosts[i].host, vm->hosts[i].user);
  for (uint32_t i = 0; i < vm->nhost_types; i++)
    if (vm->host_types[i].check)
      tvm_host_iface(rl->nv, vm->host_types[i].name, vm->host_types[i].check);
    else
      tvm_host_type(rl->nv, vm->host_types[i].name, vm->host_types[i].type_id);
  if (tvm_load_module(rl->nv, module, size) != TVM_OK) {
    tvm_set_error(vm, "new version: %s", rl->nv->error);
    reload_free(rl);
    return TVM_LOAD_ERROR;
  }
  TcsReader r = {plan, plan + plan_size, 0};
  const char *err =
      tcs_model_read_strings(&r, &rl->plan_strings, &rl->nplan_strings);
  if (!err)
    err = tcs_plan_read(&r, (const char *const *)rl->plan_strings,
                        rl->nplan_strings, &rl->plan);
  if (err) {
    tvm_set_error(vm, "reload plan: %s", err);
    reload_free(rl);
    return TVM_LOAD_ERROR;
  }
  /* 2. 計画 */
  if (rl->plan.nreasons) {
    tvm_set_error(vm, "restart required:");
    for (uint32_t i = 0; i < rl->plan.nreasons; i++)
      tvm_append_error(vm, "%s %s", i ? ";" : "", rl->plan.reasons[i]);
    reload_free(rl);
    return TVM_RESTART;
  }
  const char *mismatch = plan_types(rl);
  if (mismatch) {
    tvm_set_error(vm,
                  "reload plan does not match the new version: type %s changed "
                  "its layout",
                  mismatch);
    reload_free(rl);
    return TVM_LOAD_ERROR;
  }
  TvmStatus st = plan_heap(rl);
  if (st != TVM_OK) {
    reload_free(rl);
    return st;
  }
  /* 3. 反映 */
  vm->nreload_roots = 0;
  reflect(rl);
  /* 4. initializer、5. hook */
  run_initializers(rl);
  run_hooks(rl);
  vm->nreload_roots = 0;
  snprintf(vm->error, sizeof vm->error, "%s", rl->warnings);
  reload_free(rl);
  return TVM_OK;
}

TvmStatus tvm_apply_batch(TvmVm *vm, const uint8_t *batch, size_t size) {
  TcsReader r = {batch, batch + size, 0};
  uint32_t m = tcs_rd_u32(&r);
  if (r.bad || (size_t)(r.end - r.p) < (size_t)m + 4) {
    tvm_set_error(vm, "truncated reload batch");
    return TVM_LOAD_ERROR;
  }
  const uint8_t *module = r.p;
  r.p += m;
  uint32_t n = tcs_rd_u32(&r);
  if (r.bad || (size_t)(r.end - r.p) != (size_t)n) {
    tvm_set_error(vm, "truncated reload batch");
    return TVM_LOAD_ERROR;
  }
  return tvm_apply(vm, module, m, r.p, n);
}
