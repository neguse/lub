/* tcsvm の内部の型。読み込み (tcsvm_load.c)、runtime (tcsvm_rt.c)、interpreter (tcsvm_run.c) が共有する。 */
#ifndef TCSVM_INTERNAL_H
#define TCSVM_INTERNAL_H

#include "tcsvm.h"
#include "tcsvm_ops.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define TVM_MAGIC 0x42534354u /* "TCSB" */
#define TVM_VERSION 2u

/* runtime から interpreter に入れ子で入る深さの上限 (List.Sort の比較が Sort を呼ぶなど)。
   VM の frame は vm->frames で数えるが、入れ子は C の stack も使う */
#define TVM_MAX_DEPTH 200

/* 分岐の相対 offset: bit 40..63 の符号付き 24 bit。signed の shift を使わずに符号拡張する */
#define OFF24_OF(ins) ((int32_t)((uint32_t)((ins) >> 40) ^ 0x800000u) - 0x800000)

typedef struct TvmRegInfo {
  uint8_t kind;
  uint16_t type; /* ref の型 (TVM_NO_TYPE は null だけを持つ register など) */
} TvmRegInfo;

typedef struct TvmLeaf {
  uint8_t kind;
  uint16_t type;
  uint32_t offset; /* link が決める byte offset */
} TvmLeaf;

typedef struct TvmType {
  uint8_t tkind;
  const char *name;
  /* class / struct (cell も class) */
  uint16_t nleaves;
  TvmLeaf *leaves;
  uint32_t size;  /* class: object の大きさ (header 込み)。struct: 値の大きさ */
  uint32_t align; /* struct の alignment */
  /* 配列 / List の要素、Dictionary の値 */
  uint8_t elem_kind; /* TVM_K_* か TVM_K_STRUCT */
  uint16_t elem_type;
  uint32_t elem_size;
  /* Dictionary の key (TVM_K_I32 か、string の TVM_K_REF) */
  uint8_t key_kind;
  uint16_t key_type;
  /* 関数型: 引数と戻り値の葉 */
  uint16_t nparams, nrets;
  TvmRegInfo *params, *rets;
} TvmType;

/* ---- heap の object (先頭は必ず型) ---- */

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

typedef struct TvmString {
  const TvmType *type;
  int32_t len;
  int32_t pad;
  /* 後ろに len byte と終端の '\0' */
} TvmString;

#define STR_DATA(s) ((char *)(s) + sizeof(TvmString))

typedef struct TvmList {
  const TvmType *type;
  int32_t count, cap;
  char *data;
} TvmList;

typedef struct TvmDictEntry {
  uint32_t hash; /* 0 は空き */
  TvmSlot key;
  TvmSlot value;
} TvmDictEntry;

typedef struct TvmDict {
  const TvmType *type;
  int32_t count, cap; /* cap は 2 の冪 */
  TvmDictEntry *entries;
} TvmDict;

struct TvmProto;

typedef struct TvmClosure {
  const TvmType *type;
  const struct TvmProto *proto;
  uint32_t ncaps;
  uint32_t pad;
  /* 後ろに ncaps 個の TvmSlot */
} TvmClosure;

#define CLO_CAPS(c) ((TvmSlot *)((char *)(c) + sizeof(TvmClosure)))

/* ---- 関数 ---- */

typedef struct TvmLine {
  uint32_t pc;
  uint32_t line;
} TvmLine;

typedef struct TvmProto {
  const char *name;
  const char *file;
  uint16_t nparams;
  uint16_t nret;
  uint16_t nregs;
  uint16_t ncaps;            /* lambda の捕捉の数 (lambda は regs[0] が自分の closure) */
  uint16_t zero_lo, zero_hi; /* 呼び出し時に null で埋める ref register の範囲 */
  TvmRegInfo *regs;
  TvmRegInfo *rets;
  TvmRegInfo *caps;
  uint32_t ncode;
  uint64_t *code;
  uint32_t nlines;
  TvmLine *lines;
} TvmProto;

typedef struct TvmFrame {
  const uint64_t *pc;
  TvmSlot *R;
  const TvmProto *proto;
  const struct TvmClosure *clo; /* lambda の frame の自分の closure (LDCAP が読む) */
  uint32_t ret;                 /* 戻り値を書く caller の register */
} TvmFrame;

typedef struct TvmChunk {
  struct TvmChunk *next;
  size_t used, cap;
} TvmChunk;

struct TvmVm {
  /* module */
  uint32_t nstrings;
  char **strings;
  uint32_t nconsts;
  TvmString **consts;
  uint32_t ntypes;
  TvmType *types;
  uint16_t string_type; /* string 型の番号 (無ければ TVM_NO_TYPE) */
  uint32_t nstatics;
  TvmRegInfo *static_info;
  TvmSlot *statics;
  uint32_t nfuncs;
  TvmProto *funcs;
  int32_t entry;
  /* 実行。top / ftop は入れ子の呼び出し (runtime から closure を呼ぶ) が使う次の空き */
  TvmSlot *stack, *stack_end, *top;
  TvmFrame *frames, *frames_end, *ftop;
  bool faulting; /* fault の trace を書いている途中 (外側の frame は追記する) */
  uint32_t depth; /* runtime から入れ子に入った深さ (C の stack を使うので上限を置く) */
  /* heap: GC はまだ無く bump 確保だけ (doc/vm-design.md §10) */
  TvmChunk *heap;
  size_t heap_bytes;
  char error[4096];
};

/* tcsvm_load.c */
void tvm_set_error(TvmVm *vm, const char *fmt, ...);
void tvm_append_error(TvmVm *vm, const char *fmt, ...);

/* tcsvm_rt.c */
void *tvm_alloc(TvmVm *vm, size_t size);
void tvm_heap_free(TvmVm *vm);
TvmString *tvm_str_new(TvmVm *vm, const char *data, size_t len);
TvmString *tvm_str_concat(TvmVm *vm, const TvmString *a, const TvmString *b);
bool tvm_str_eq(const TvmString *a, const TvmString *b);
TvmList *tvm_list_new(TvmVm *vm, const TvmType *type);
bool tvm_list_grow(TvmVm *vm, TvmList *list);
TvmDict *tvm_dict_new(TvmVm *vm, const TvmType *type);
TvmDictEntry *tvm_dict_find(const TvmDict *d, TvmSlot key);
TvmDictEntry *tvm_dict_insert(TvmVm *vm, TvmDict *d, TvmSlot key);
uint32_t tvm_kind_size(uint8_t kind);

/* tcsvm_run.c */
TvmStatus tvm_run(TvmVm *vm, const TvmProto *proto, const TvmSlot *args, TvmSlot *rets);
/* 入れ子の呼び出し: closure を args で呼ぶ (runtime の List.Sort から) */
TvmStatus tvm_call_closure(TvmVm *vm, const TvmClosure *c, const TvmSlot *args, TvmSlot *rets);

/* tcsvm_rt.c の List.Sort */
TvmStatus tvm_list_sort(TvmVm *vm, TvmList *list, const TvmClosure *compare);

#endif
