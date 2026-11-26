/* Heap walker and reload plan (docs/tcs/object-model.md §6, §7). The VM and
   tcs2c use the same code. The walker follows the reference locations inside a
   value by looking only at the type descriptors. The layout of object
   containers (arrays / List / Dictionary) is owned by the implementation (to be
   aligned in M-4), which passes the values inside to this walker. */
#ifndef TCS_WALK_H
#define TCS_WALK_H

#include "tcs_model.h"

/* Called for each reference location (the sink for GC marking, re-pointing
 * during migration, and snapshot writing) */
typedef void (*TcsRefFn)(void *ctx, void **slot);

/* Reference locations inside the value base of type t (a class payload or a
 * struct value) */
TCS_MODEL_API void tcs_walk_value(const TcsType *t, char *base, TcsRefFn fn,
                                  void *ctx);

/* n elements (element kind and type, size). Elements of arrays / List, values
 * of Dictionary */
TCS_MODEL_API void tcs_walk_elems(const TcsType *types, uint8_t kind,
                                  uint16_t type, uint32_t size, char *data,
                                  uint32_t n, TcsRefFn fn, void *ctx);

/* ---- Reload plan (§6) ---- */

enum {
  TCS_PLAN_SAME = 0,
  TCS_PLAN_ADDED = 1,
  TCS_PLAN_REMOVED = 2,
  TCS_PLAN_RELAYOUT = 3
};
enum { TCS_FIELD_RETAINED = 0, TCS_FIELD_ADDED = 1, TCS_FIELD_RESET = 2 };

typedef struct TcsPlanField {
  uint8_t source;     /* TCS_FIELD_* */
  uint16_t old_field; /* RETAINED: field number in the old version */
  uint32_t init; /* ADDED: initializer function number + 1 (0 means the zero
                    value) */
} TcsPlanField;

typedef struct TcsPlanType {
  const char *name;
  uint8_t change;   /* TCS_PLAN_* */
  uint16_t nfields; /* RELAYOUT: number of new fields */
  TcsPlanField *fields;
} TcsPlanType;

typedef struct TcsPlanStatic {
  const char *owner, *field;
  uint8_t change; /* TCS_PLAN_ADDED / REMOVED / SAME (= retained) */
} TcsPlanStatic;

typedef struct TcsPlan {
  uint32_t nreasons;
  const char **reasons; /* reasons for a restart (if there is even one, the plan
                           is not applied) */
  uint32_t ntypes;
  TcsPlanType *types;
  uint32_t nstatics;
  TcsPlanStatic *statics;
} TcsPlan;

/* Reads a plan (names are indices into strings). On failure returns a message
 */
TCS_MODEL_API const char *tcs_plan_read(TcsReader *r,
                                        const char *const *strings,
                                        uint32_t nstrings, TcsPlan *plan);
TCS_MODEL_API void tcs_plan_free(TcsPlan *plan);
TCS_MODEL_API const TcsPlanType *tcs_plan_type(const TcsPlan *plan,
                                               const char *name);

/* Value migration: copies the value src of old-version type ot into the value
   dst of new-version type nt following the plan. dst must be zero-filled.
   RETAINED fields are copied if they have the same kind, and struct fields
   recurse with the struct's plan. ADDED / RESET stay 0 (the caller writes the
   initializers later) */
TCS_MODEL_API void tcs_migrate_value(const TcsPlan *plan,
                                     const TcsType *oldtypes, const TcsType *ot,
                                     const char *src, const TcsType *newtypes,
                                     const TcsType *nt, char *dst);

#endif
