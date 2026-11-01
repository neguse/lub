/* host の関数と、host が VM の object を読み書きする API (tcsvm.h、doc/vm-design.md §8.5、§13)。 */
#include "tcsvm_internal.h"

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
  return object ? TVM_TYPE(((const TvmObject *)object))->name : NULL;
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
