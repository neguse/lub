/* heap の walker と reload の計画 (doc/object-model.md §6、§7)。VM と tcs2c が同じものを使う。
   walker は型記述子だけを見て値の中の参照の場所を辿る。object の容器 (配列 / List / Dictionary) の
   並べ方は実装が持ち (M-4 で揃える)、中の値をこの walker に渡す。 */
#ifndef TCS_WALK_H
#define TCS_WALK_H

#include "tcs_model.h"

/* 参照の場所ごとに呼ぶ (GC の mark、移行の張り替え、snapshot の書き出しの sink) */
typedef void (*TcsRefFn)(void *ctx, void **slot);

/* 型 t (class の payload か struct の値) の値 base の中の参照の場所 */
TCS_MODEL_API void tcs_walk_value(const TcsType *t, char *base, TcsRefFn fn,
                                  void *ctx);

/* 要素 n 個 (要素の種類と型、大きさ)。配列 / List の要素、Dictionary の値 */
TCS_MODEL_API void tcs_walk_elems(const TcsType *types, uint8_t kind,
                                  uint16_t type, uint32_t size, char *data,
                                  uint32_t n, TcsRefFn fn, void *ctx);

/* ---- reload の計画 (§6) ---- */

enum {
  TCS_PLAN_SAME = 0,
  TCS_PLAN_ADDED = 1,
  TCS_PLAN_REMOVED = 2,
  TCS_PLAN_RELAYOUT = 3
};
enum { TCS_FIELD_RETAINED = 0, TCS_FIELD_ADDED = 1, TCS_FIELD_RESET = 2 };

typedef struct TcsPlanField {
  uint8_t source;     /* TCS_FIELD_* */
  uint16_t old_field; /* RETAINED: 旧版の field の番号 */
  uint32_t init;      /* ADDED: initializer の関数の番号 + 1 (0 は 0 の値) */
} TcsPlanField;

typedef struct TcsPlanType {
  const char *name;
  uint8_t change;   /* TCS_PLAN_* */
  uint16_t nfields; /* RELAYOUT: 新しい field の数 */
  TcsPlanField *fields;
} TcsPlanType;

typedef struct TcsPlanStatic {
  const char *owner, *field;
  uint8_t change; /* TCS_PLAN_ADDED / REMOVED / SAME (= retained) */
} TcsPlanStatic;

typedef struct TcsPlan {
  uint32_t nreasons;
  const char **reasons; /* restart の理由 (1 つでもあれば適用しない) */
  uint32_t ntypes;
  TcsPlanType *types;
  uint32_t nstatics;
  TcsPlanStatic *statics;
} TcsPlan;

/* 計画を読む (名前は strings の番号)。失敗したら message を返す */
TCS_MODEL_API const char *tcs_plan_read(TcsReader *r,
                                        const char *const *strings,
                                        uint32_t nstrings, TcsPlan *plan);
TCS_MODEL_API void tcs_plan_free(TcsPlan *plan);
TCS_MODEL_API const TcsPlanType *tcs_plan_type(const TcsPlan *plan,
                                               const char *name);

/* 値の移行: 旧版の型 ot の値 src を、新版の型 nt の値 dst へ計画に従って写す。dst は 0 で埋まっていること。
   RETAINED の field は同じ種類なら写し、struct の field は struct の計画で再帰する。ADDED / RESET は 0 のまま
   (initializer は呼ぶ側が後で書く) */
TCS_MODEL_API void tcs_migrate_value(const TcsPlan *plan,
                                     const TcsType *oldtypes, const TcsType *ot,
                                     const char *src, const TcsType *newtypes,
                                     const TcsType *nt, char *dst);

#endif
