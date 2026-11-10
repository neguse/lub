/* tcs_walk.h の実装 (doc/object-model.md §6、§7)。 */
#include "tcs_walk.h"

#include <stdlib.h>
#include <string.h>

TCS_MODEL_API void tcs_walk_value(const TcsType *t, char *base, TcsRefFn fn,
                                  void *ctx) {
  for (uint32_t k = 0; k < t->nleaves; k++)
    if (t->leaves[k].kind == TCS_K_REF)
      fn(ctx, (void **)(base + t->leaves[k].offset));
}

TCS_MODEL_API void tcs_walk_elems(const TcsType *types, uint8_t kind,
                                  uint16_t type, uint32_t size, char *data,
                                  uint32_t n, TcsRefFn fn, void *ctx) {
  if (kind == TCS_K_REF) {
    for (uint32_t i = 0; i < n; i++)
      fn(ctx, (void **)(data + (size_t)i * size));
  } else if (kind == TCS_K_STRUCT) {
    const TcsType *st = &types[type];
    for (uint32_t i = 0; i < n; i++)
      tcs_walk_value(st, data + (size_t)i * size, fn, ctx);
  }
}

static const char *plan_name(TcsReader *r, const char *const *strings,
                             uint32_t nstrings) {
  uint32_t i = tcs_rd_u32(r);
  if (i >= nstrings) {
    r->bad = 1;
    return "?";
  }
  return strings[i];
}

TCS_MODEL_API const char *tcs_plan_read(TcsReader *r,
                                        const char *const *strings,
                                        uint32_t nstrings, TcsPlan *plan) {
  memset(plan, 0, sizeof *plan);
#define NAME(ignored) plan_name(r, strings, nstrings)
  uint32_t n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 4))
    return "truncated plan";
  plan->reasons = (const char **)calloc(n ? n : 1, sizeof(char *));
  if (!plan->reasons)
    return "out of memory";
  plan->nreasons = n;
  for (uint32_t i = 0; i < n; i++)
    plan->reasons[i] = NAME(tcs_rd_u32(r));
  n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 5))
    return "truncated plan";
  plan->types = (TcsPlanType *)calloc(n ? n : 1, sizeof(TcsPlanType));
  if (!plan->types)
    return "out of memory";
  plan->ntypes = n;
  for (uint32_t i = 0; i < n && !r->bad; i++) {
    TcsPlanType *t = &plan->types[i];
    t->name = NAME(tcs_rd_u32(r));
    t->change = tcs_rd_u8(r);
    if (t->change != TCS_PLAN_RELAYOUT)
      continue;
    t->nfields = tcs_rd_u16(r);
    if (!tcs_rd_count_ok(r, t->nfields, 7))
      return "truncated plan";
    t->fields = (TcsPlanField *)calloc(t->nfields ? t->nfields : 1,
                                       sizeof(TcsPlanField));
    if (!t->fields)
      return "out of memory";
    for (uint32_t k = 0; k < t->nfields; k++) {
      t->fields[k].source = tcs_rd_u8(r);
      t->fields[k].old_field = tcs_rd_u16(r);
      t->fields[k].init = tcs_rd_u32(r);
    }
  }
  n = tcs_rd_u32(r);
  if (!tcs_rd_count_ok(r, n, 9))
    return "truncated plan";
  plan->statics = (TcsPlanStatic *)calloc(n ? n : 1, sizeof(TcsPlanStatic));
  if (!plan->statics)
    return "out of memory";
  plan->nstatics = n;
  for (uint32_t i = 0; i < n; i++) {
    plan->statics[i].owner = NAME(tcs_rd_u32(r));
    plan->statics[i].field = NAME(tcs_rd_u32(r));
    plan->statics[i].change = tcs_rd_u8(r);
  }
#undef NAME
  return r->bad ? "truncated plan" : NULL;
}

TCS_MODEL_API void tcs_plan_free(TcsPlan *plan) {
  for (uint32_t i = 0; plan->types && i < plan->ntypes; i++)
    free(plan->types[i].fields);
  free(plan->types);
  free(plan->reasons);
  free(plan->statics);
  memset(plan, 0, sizeof *plan);
}

TCS_MODEL_API const TcsPlanType *tcs_plan_type(const TcsPlan *plan,
                                               const char *name) {
  for (uint32_t i = 0; i < plan->ntypes; i++)
    if (strcmp(plan->types[i].name, name) == 0)
      return &plan->types[i];
  return NULL;
}

static void copy_field(const TcsPlan *plan, const TcsType *oldtypes,
                       const TcsField *of, const char *src,
                       const TcsType *newtypes, const TcsField *nf, char *dst) {
  if (of->kind != nf->kind)
    return;
  if (nf->kind == TCS_K_STRUCT) {
    tcs_migrate_value(plan, oldtypes, &oldtypes[of->type], src + of->offset,
                      newtypes, &newtypes[nf->type], dst + nf->offset);
    return;
  }
  memcpy(dst + nf->offset, src + of->offset,
         tcs_kind_size(nf->kind, (uint32_t)sizeof(void *)));
}

TCS_MODEL_API void tcs_migrate_value(const TcsPlan *plan,
                                     const TcsType *oldtypes, const TcsType *ot,
                                     const char *src, const TcsType *newtypes,
                                     const TcsType *nt, char *dst) {
  const TcsPlanType *pt = tcs_plan_type(plan, nt->name);
  if (!pt || pt->change != TCS_PLAN_RELAYOUT) {
    /* 形の変わらない型: field を順に写す (struct の field は中の計画で) */
    for (uint32_t k = 0; k < nt->nfields && k < ot->nfields; k++)
      copy_field(plan, oldtypes, &ot->fields[k], src, newtypes, &nt->fields[k],
                 dst);
    return;
  }
  for (uint32_t k = 0; k < pt->nfields && k < nt->nfields; k++)
    if (pt->fields[k].source == TCS_FIELD_RETAINED &&
        pt->fields[k].old_field < ot->nfields)
      copy_field(plan, oldtypes, &ot->fields[pt->fields[k].old_field], src,
                 newtypes, &nt->fields[k], dst);
}
