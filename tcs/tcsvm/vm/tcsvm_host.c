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

int tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets, TvmHostFn fn, void *user) {
  /* 読み込んだ後に足すと、module が持つ host の関数の pointer がずれる */
  if (vm->nfuncs || !name || !params || !rets || !fn)
    return 0;
  for (const char *p = params; *p; p++)
    if (!strchr("ifbso", *p))
      return 0;
  for (const char *p = rets; *p; p++)
    if (!strchr("ifbso", *p))
      return 0;
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

static const TvmType *find_type(TvmVm *vm, const char *name) {
  for (uint32_t i = 0; i < vm->ntypes; i++)
    if (strcmp(vm->types[i].name, name) == 0)
      return &vm->types[i];
  return NULL;
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
    TVM_TYPE(o) = t;
    o->type_id = (uint32_t)(t - vm->types);
  return o;
}

const char *tvm_object_type(const void *object) {
  if (!object || GC_HEADER(object)->kind != TVM_KIND_OBJECT)
    return NULL; /* string と C の module の object */
  return TVM_TYPE(((const TvmObject *)object))->name;
}

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

static char sig_letter(TvmVm *vm, const TvmRegInfo *info) {
  switch (info->kind) {
  case TVM_K_I32:
    return 'i';
  case TVM_K_F32:
    return 'f';
  case TVM_K_BOOL:
    return 'b';
  default:
    return info->type == vm->string_type ? 's' : 'o';
  }
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
  for (uint32_t i = 0; i < vm->ndelegates; i++)
    if (strcmp(vm->delegates[i].sig, sig) == 0)
      return vm->delegates[i].fn;
  return NULL;
}
