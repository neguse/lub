/* tcsvm の内部の型。読み込み (tcsvm_load.c)、runtime (tcsvm_rt.c)、interpreter (tcsvm_run.c) が共有する。 */
#ifndef TCSVM_INTERNAL_H
#define TCSVM_INTERNAL_H

#include "../../runtime/model/tcs_model.h"
#include "tcsvm.h"
#include "tcsvm_ops.h"

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#define TVM_MAGIC 0x42534354u /* "TCSB" */
#define TVM_VERSION 4u

/* runtime から interpreter に入れ子で入る深さの上限 (List.Sort の比較が Sort を呼ぶなど)。
   VM の frame は vm->frames で数えるが、入れ子は C の stack も使う */
#define TVM_MAX_DEPTH 200

/* 分岐の相対 offset: bit 40..63 の符号付き 24 bit。signed の shift を使わずに符号拡張する */
#define OFF24_OF(ins)                                                          \
  ((int32_t)((uint32_t)((ins) >> 40) ^ 0x800000u) - 0x800000)

/* 型記述子は VM と tcs2c が共有する形 (runtime/model、doc/object-model.md §4) */
typedef TcsInfo TvmRegInfo;
typedef TcsLeaf TvmLeaf;
typedef TcsType TvmType;
typedef TcsItable TvmItable;

/* static の field ("型.field"、先頭の slot、種類と型)。reload は名前で旧版の static と対応させる。
   struct の static は slot から葉を並べる */
typedef struct TvmStaticField {
  const char *name;
  uint32_t slot;
  uint8_t kind;
  uint16_t type;
} TvmStaticField;

typedef struct TvmIntrinsic TvmIntrinsic;
typedef struct TvmHostPlan TvmHostPlan;

/* interface の method の呼び出し先の名前と関数型 (CALLI の #c) */
typedef struct TvmSelector {
  const char *name;
  uint16_t functype;
} TvmSelector;

/* ---- heap の object ----
   型は GC の header が持つ (doc/object-model.md §3、TVM_TYPE)。class の payload は tcs2c と同じく
   { uint32_t type_id; field... } で、field の offset は tcs2c の生成 struct と同じになる (§5) */

typedef struct TvmObject {
  uint32_t type_id; /* 型の番号 (vm->types の添字) */
} TvmObject;

typedef struct TvmArray {
  int32_t len;
  int32_t pad;
} TvmArray;

#define ARR_DATA(a) ((char *)(a) + sizeof(TvmArray))
#define CLASS_HEADER ((uint32_t)sizeof(TvmObject))

/* tcs2c の TcsString ({ size_t length; data[] }) と同じ byte 並び (little endian、pad は常に 0。len は 2^31 未満):
   shared heap では C の module と string を写さずに渡す (doc/object-model.md §10.6) */
typedef struct TvmString {
  int32_t len;
#if SIZE_MAX > 0xFFFFFFFFu
  int32_t pad; /* 64 bit: size_t の上位 (32 bit の size_t は len だけ) */
#endif
  /* 後ろに len byte と終端の '\0' */
} TvmString;
_Static_assert(sizeof(TvmString) == sizeof(size_t),
               "TvmString must match TcsString");
#if defined(__BYTE_ORDER__) && __BYTE_ORDER__ != __ORDER_LITTLE_ENDIAN__
#error                                                                         \
    "tcsvm assumes little endian (TvmString shares its layout with TcsString)"
#endif

#define STR_DATA(s) ((char *)(s) + sizeof(TvmString))

typedef struct TvmList {
  int32_t count, cap;
  char *data;
} TvmList;

/* Dictionary: 足した順の entry の列 (列挙は挿入順。support-matrix §18。消した entry は live = 0 の穴で、広げるときに詰める) と、
   entry の番号を引く open addressing の表。entry は { hash, live, key, 値 (型の葉の並び) } */
typedef struct TvmDictEntry {
  uint32_t hash;
  uint32_t live;
  TvmSlot key;
  /* 後ろに値 (type->elem_size byte、8 に揃える) */
} TvmDictEntry;

#define DICT_VALUE(e) ((char *)(e) + sizeof(TvmDictEntry))

typedef struct TvmDict {
  int32_t count; /* 生きている entry */
  int32_t used;  /* 積んだ entry (消したものを含む) */
  int32_t cap;   /* entry の列の容量 */
  int32_t nbuckets;
  int32_t
      version; /* 新しい key を足すか Clear するたびに増える (列挙の途中の追加を foreach が fault にする) */
  char *entries;    /* blob */
  int32_t *buckets; /* blob: entry の番号 + 1。0 は空き、-1 は消した */
} TvmDict;

#define DICT_ENTRY(d, i)                                                       \
  ((TvmDictEntry *)((d)->entries + (size_t)(i) * TVM_TYPE(d)->dict_entry))

typedef struct TvmBox {
  TvmSlot value;
} TvmBox;

struct TvmProto;

typedef struct TvmClosure {
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

/* --trace の代入の値の場所 (TVAL の #c)。buf は最後に控えた葉 */
typedef struct TvmTraceVal {
  const char *file, *label;
  uint32_t line, col;
  uint8_t kind;
  uint16_t type;
  uint16_t nleaves;
  bool set;
  TvmSlot *buf;
} TvmTraceVal;

/* module の後ろの表 (tcsvm_debug.c)。名前は module の string 表を指す */
typedef struct TvmDebug {
  const char **func_paths; /* 関数ごとのファイルの path */
  uint32_t nlines;
  const char **line_files;
  uint32_t *line_nums;
  uint8_t *hit;   /* 行ごとの通った印 */
  uint32_t *hits; /* 印を付けた行 */
  uint32_t nhits;
  /* class の instance の method の行を通った (行, self) の組 (前に取り出してから)。self は比べるだけで読まない。
     型は組を記録したときの self の型 (self が回収されても読まずに済む) */
  uint32_t *pair_line;
  void **pair_self;
  const TvmType **pair_type;
  uint32_t npairs, cappairs;
  uint32_t *
      pair_slot; /* 組の重複を見る表 (組の番号 + 1、0 は空き)。大きさは cappairs * 2 */
  uint8_t *line_flags; /* 取り出すときの、行ごとの self の分け方 (nlines 個) */
  uint32_t nvals;
  TvmTraceVal *vals;
  uint32_t *dirty; /* 控えた値の場所 */
  uint32_t ndirty;
  uint32_t nclasses;
  const char **classes; /* --trace のファイルの class */
  /* 負荷 (tvm_profile_*): 行ごとの時間 (ns) と確保 (byte と回数)、足した印と足した行、今の行 + 1 (0 は行の外) */
  uint64_t *prof_ns;
  uint64_t *prof_bytes;
  uint32_t *prof_allocs;
  uint8_t *prof_hit;
  uint32_t *prof_hits;
  uint32_t nprof_hits;
  uint32_t prof_cur;
  /* 前の frame の境目の後に --trace の行で作った object と、作った行 (pointer の開番地法、cap は 2 の冪) */
  void **prof_new;
  uint32_t *prof_new_line;
  uint32_t nprof_new, capprof_new;
} TvmDebug;

/* 状態の控え (tcsvm_debug.c) */
typedef struct TvmSnapObj {
  void *p;
  void *bytes;
  size_t size;
} TvmSnapObj;

typedef struct TvmSnapStatic {
  uint32_t slot;
  TvmSlot value;
} TvmSnapStatic;

struct TvmSnapshot {
  struct TvmSnapshot *next;
  uint32_t layout_version;
  TvmSnapObj *objs;
  uint32_t nobjs, capobjs;
  TvmSnapStatic *statics;
  uint32_t nstatics, capstatics;
  /* object → objs の添字 (open addressing) */
  void **seen_keys;
  uint32_t *seen_vals;
  uint32_t seen_cap;
};

typedef struct TvmPinned {
  char key[64];
  void *p;
} TvmPinned;

typedef struct TvmProto {
  const char *name;
  const char *file;
  uint16_t nparams;
  uint16_t nret;
  uint16_t nregs;
  uint16_t ncaps; /* lambda の捕捉の数 (lambda は regs[0] が自分の closure) */
  uint16_t zero_lo,
      zero_hi; /* 呼び出し時に null で埋める ref register の範囲 */
  TvmRegInfo *regs;
  TvmRegInfo *rets;
  TvmRegInfo *caps;
  uint32_t ncode;
  uint64_t *code;
  uint32_t nlines;
  TvmLine *lines;
  uint16_t
      trace_self; /* --trace の class の instance の method なら self の register + 1 (TRACE の A。それ以外は 0) */
} TvmProto;

typedef struct TvmFrame {
  const uint64_t *pc;
  TvmSlot *R;
  const TvmProto *proto;
  const struct TvmClosure
      *clo;     /* lambda の frame の自分の closure (LDCAP が読む) */
  uint32_t ret; /* 戻り値を書く caller の register */
  uint32_t
      line; /* 負荷を数えている間の、呼んだときの行 + 1 (戻ったらこの行に戻す) */
} TvmFrame;

/* heap の object の前に置く GC の header (doc/vm-design.md §10)。
   blob は型を持たない buffer (List の要素、Dictionary の entry) で、持ち主の object が辿る。
   fixed は回収しない object (string の定数) */
/* runtime/core の TcsGcHeader と同じ形 (doc/object-model.md §3、§10.2)。shared heap (tvm_use_heap) では
   tcs2c の GC がこの header で VM の object を扱う: kind は TVM_KIND_OBJECT (= TCS_KIND_FOREIGN) か
   TVM_KIND_BLOB (= TCS_KIND_RAW)、type は TcsGcHeader の layout の場所 */
/* MSVC の C (clang-cl でない cl.exe) の stddef.h は max_align_t を持たない。最大の alignment は double と同じ */
#if defined(_MSC_VER) && !defined(__clang__)
typedef double tvm_max_align_t;
#else
typedef max_align_t tvm_max_align_t;
#endif

typedef struct TvmGcHeader {
  _Alignas(tvm_max_align_t) struct TvmGcHeader *next;
  const TvmType *
      type; /* 型 (blob は NULL)。reload で作り直した object は転送先 (TVM_GC_RELOADED) */
  size_t size; /* payload の byte 数 */
  uint16_t kind;
  uint16_t flags;
  uint32_t type_id;
} TvmGcHeader;

#define TVM_KIND_BLOB 0u      /* TCS_KIND_RAW */
#define TVM_KIND_OBJECT 0x40u /* TCS_KIND_FOREIGN */
#define TVM_GC_MARK 1u        /* 自前の GC の mark (TCS_GC_MARK と同じ値) */
#define TVM_GC_STATIC                                                          \
  2u /* 回収しない object (string の定数。TCS_GC_STATIC と同じ値) */
#define TVM_GC_RELOADED                                                        \
  0x100u /* reload の移行で作り直した旧 object (tcs の flags と重ならない bit) */
#define TVM_GC_NEW                                                             \
  0x200u /* 負荷を数えている間に --trace の行で作った object (持ち越しを数える) */
#define TVM_GC_SEEN 0x400u /* 持ち越しを数える間に辿った印 */
#define GC_BLOB(h) ((h)->kind == TVM_KIND_BLOB)

#define GC_HEADER(p) ((TvmGcHeader *)(p) - 1)
#define TVM_TYPE(p) (GC_HEADER(p)->type)
#define TVM_MAX_PINS 1024

struct TvmVm {
  /* module */
  uint32_t nstrings;
  char **strings;
  uint32_t nconsts;
  TvmString **consts;
  uint32_t ntypes;
  TvmType *types;
  uint16_t string_type; /* string 型の番号 (無ければ TVM_NO_TYPE) */
  uint32_t
      types_version; /* 型の表を読み込むか reload で入れ替えるたびに、tvm_next_types_version から新しい値を取る */
  uint32_t *
      type_ids; /* 型ごとの object の type_id (TVM_TYPE_ID_VM + 番号か、tvm_host_type の番号) */
  TvmIfaceCheck *
      iface_checks; /* 型ごとの --ref の interface の判定 (tvm_host_iface。無ければ NULL) */
  uint32_t nstatics;
  TvmRegInfo *static_info;
  TvmSlot *statics;
  uint32_t nstatic_fields;
  TvmStaticField *static_fields;
  uint32_t nfuncs;
  TvmProto *funcs;
  int32_t entry;
  uint32_t nselectors;
  TvmSelector *selectors;
  uint32_t nintrinsics;
  const TvmIntrinsic **
      intrinsics; /* module の runtime 関数の番号 → tvm_intrinsics か host の関数 */
  /* intrinsics と同じ番号の、struct か out を写す host の関数の呼び方 (module の型で解く。写さない関数は NULL) */
  TvmHostPlan **host_plans;
  /* host の関数 (tvm_host。名前と種類の列は複製して持つ) */
  TvmIntrinsic *hosts;
  uint32_t nhosts, caphosts;
  /* C の module の型 (tvm_host_type / tvm_host_iface) */
  struct TvmHostType {
    char *name;
    uint32_t type_id;
    TvmIfaceCheck check;
  } *host_types;
  uint32_t nhost_types;
  /* C の module の delegate の invoke (tvm_host_delegate) */
  struct {
    char sig[32];
    TvmDelegateFn fn;
  } delegates[32];
  uint32_t ndelegates;
  /* 実行。top / ftop は入れ子の呼び出し (runtime から closure を呼ぶ) が使う次の空き */
  TvmSlot *stack, *stack_end, *top;
  TvmFrame *frames, *frames_end, *ftop;
  bool faulting; /* fault の trace を書いている途中 (外側の frame は追記する) */
  uint32_t
      depth; /* runtime から入れ子に入った深さ (C の stack を使うので上限を置く) */
  /* heap: 動かさない mark-sweep (doc/vm-design.md §10)。debt が limit に達したら、
     フレーム境界 (tvm_frame_end) か interpreter の確保の地点で回収する */
  TvmGcHeader *objects;
  size_t gc_debt, gc_limit, gc_live, gc_count;
  bool
      gc_stress; /* 確保の地点とフレーム境界ごとに回収する (root の漏れを出力の違いで見つける) */
  void **mark_stack;
  size_t nmark, capmark;
  /* runtime の C が持つ参照の場所 (List.Sort の作業領域と並べている buffer) */
  void **pins[TVM_MAX_PINS];
  uint32_t npins;
  /* runtime の C が持つ struct の値の場所 (List.Sort の作業領域)。GC が葉の型で辿る */
  struct {
    const char *base;
    const TvmType *st;
    uint32_t count;
  } struct_pins[TVM_MAX_DEPTH];
  uint32_t nstruct_pins;
  /* reload: 移行した object と hook を呼ぶ object (initializer と hook の間の root)、
     前の版の string の定数 (heap から指されうるので VM の寿命の間残す) */
  void **reload_roots;
  size_t nreload_roots, capreload_roots;
  uint32_t nretired;
  TvmString **retired;
  /* host が持つ object (tvm_hold)。空きは NULL */
  void **handles;
  uint32_t nhandles, caphandles;
  /* shared heap (tvm_use_heap): 確保と回収を tcs2c の runtime に任せる */
  const TvmHeap *heap;
  /* 検査 (tcsvm_debug.c): module の後ろの表 (reload で入れ替える)、状態の控え、弱く覚えた object */
  TvmDebug *debug;
  struct TvmSnapshot *snapshots;
  uint32_t layout_version;
  TvmPinned *pinned;
  uint32_t npinned, cappinned;
  /* 負荷 (tcsvm_debug.c): 時計 (NULL なら数えない)、知らされてまだ行に振り分けていない時間 (別の thread も足す)、
     interpreter の入れ子の深さ、GC の止まり */
  TvmClockFn prof_clock;
  volatile uint64_t prof_pending;
  uint32_t prof_depth;
  struct {
    uint64_t ns;
    size_t live;
  } prof_gc[TVM_PROFILE_GCS];
  uint32_t nprof_gc;
  char error[4096];
};

/* tcsvm_debug.c */
/* 型の表の新しい版 (VM をまたいで一意。tcsvm_host.c) */
uint32_t tvm_next_types_version(void);

bool tvm_debug_read(TvmVm *vm, TcsReader *r);
void tvm_debug_free(TvmDebug *d);
uint8_t tvm_debug_val_leaf_kind(TvmVm *vm, const TvmTraceVal *v, uint32_t k);
void tvm_debug_mark(TvmVm *vm, void (*mark)(TvmVm *vm, void *p));
void tvm_debug_forget(TvmVm *vm, void *p);
/* --trace: 行 k を self で通った組を記録する (TRACE が呼ぶ) */
void tvm_debug_pair(TvmVm *vm, uint32_t k, void *self);
/* 負荷 (prof_clock があるときだけ呼ぶ): 行 k に着いた (TRACE)、interpreter に入る・出る、確保した、GC が始まる・止めた */
void tvm_profile_line(TvmVm *vm, uint32_t k);
/* 今の行 + 1 (数えていないか行の外なら 0)。呼ぶときに控え、戻ったら tvm_profile_return でこの行に戻す */
static inline uint32_t tvm_profile_at(const TvmVm *vm) {
  return vm->prof_clock && vm->debug ? vm->debug->prof_cur : 0;
}
void tvm_profile_return(TvmVm *vm, uint32_t line);
void tvm_profile_enter(TvmVm *vm);
void tvm_profile_leave(TvmVm *vm, uint32_t line);
void tvm_profile_alloc(TvmVm *vm, void *p, size_t bytes);
void tvm_profile_frame_end(TvmVm *vm);
void tvm_profile_gc_begin(TvmVm *vm);
void tvm_profile_gc(TvmVm *vm, uint64_t ns, size_t live);
/* reload が object を作り直した: 控えの中身を捨てる (控えそのものは host が持つので残す) */
void tvm_snapshots_invalidate(TvmVm *vm);
const char *tvm_func_path(TvmVm *vm, const TvmProto *p);

/* tcsvm_load.c */
/* 読み込み・検査・link まで (static initializer は走らせない)。reload は別の VM にこれで読む */
TvmStatus tvm_load_module(TvmVm *vm, const uint8_t *bytes, size_t size);
void tvm_set_error(TvmVm *vm, const char *fmt, ...);
void tvm_append_error(TvmVm *vm, const char *fmt, ...);

/* tcsvm_gc.c */
void *
tvm_alloc(TvmVm *vm,
          size_t size); /* 型を持つ object (先頭が TvmType *)。0 で埋める */
void *tvm_alloc_blob(TvmVm *vm,
                     size_t size);  /* 型を持たない buffer。0 で埋める */
void *tvm_alloc_fixed(size_t size); /* 回収しない object (定数) */
void tvm_free_fixed(void *p);
void tvm_heap_free(TvmVm *vm);
void tvm_gc_collect(TvmVm *vm);
/* object の中の参照の場所 (blob の pointer を含む) を順に fn に渡す。GC の mark と reload の張り替えが共有する */
void tvm_object_refs(TvmVm *vm, void *p, void (*fn)(void *ctx, void **slot),
                     void *ctx);
TvmString *tvm_str_new(TvmVm *vm, const char *data, size_t len);
TvmString *tvm_str_concat(TvmVm *vm, const TvmString *a, const TvmString *b);
bool tvm_str_eq(const TvmString *a, const TvmString *b);
TvmList *tvm_list_new(TvmVm *vm, const TvmType *type);
bool tvm_list_grow(TvmVm *vm, TvmList *list);
TvmDict *tvm_dict_new(TvmVm *vm, const TvmType *type);
int32_t tvm_dict_find(const TvmVm *vm, const TvmDict *d,
                      TvmSlot key); /* 無ければ -1 */
int32_t tvm_dict_insert(TvmVm *vm, TvmDict *d,
                        TvmSlot key); /* 確保に失敗したら -1 */
bool tvm_dict_remove(TvmVm *vm, TvmDict *d, TvmSlot key);
bool tvm_list_insert0(TvmVm *vm, TvmList *list, int32_t at);
void tvm_list_remove_at(TvmList *list, int32_t at);
bool tvm_value_eq(TvmVm *vm, uint8_t kind, uint16_t type, const void *a,
                  const void *b);
bool tvm_record_eq(TvmVm *vm, const TvmObject *a, const TvmObject *b);
/* 参照の値の等価 (EqualityComparer<object>.Default): 実行時の型で string と box は中の値、record は field ごと、他は同一性 */
bool tvm_object_eq(TvmVm *vm, const TvmObject *a, const TvmObject *b);
bool tvm_is_type(TvmVm *vm, const TvmObject *o, const TvmType *t);
/* class の object に型を付ける (header の型記述子と、header と payload の type_id。tcs2c の型の test が読む) */
static inline void tvm_class_init(const TvmVm *vm, TvmObject *o,
                                  const TvmType *t) {
  uint32_t id = vm->type_ids[t - vm->types];
  TVM_TYPE(o) = t;
  GC_HEADER(o)->type_id = id;
  o->type_id = id;
}
/* 読み込んだ型に C の module の型の登録を当てる */
bool tvm_bind_host_types(TvmVm *vm);
/* object の型。shared heap では C の module の string (kind RAW) は string 型、C の object は NULL (型を持たない) */
const TvmType *tvm_type_of(TvmVm *vm, const void *o);

/* runtime の関数 (CALLR)。params / rets は種類の列:
   i int、f float、b bool、s string、o 任意の ref、L List、A 配列、D Dictionary、F closure、
   e arg0 (List / 配列) の要素 (1 葉)、k arg0 (Dictionary) の key、r arg0 と同じ型。
   host の関数 (tvm_host) の列は tcsvm.h の書き方 (i f b s o、S{型}、前に & の out) */
typedef TvmStatus (*TvmIntrinsicFn)(TvmVm *vm, TvmSlot *args, TvmSlot *rets);
struct TvmIntrinsic {
  const char *name;
  const char *params;
  const char *rets;
  TvmIntrinsicFn fn;
  TvmHostFn host; /* host の関数 (tvm_host)。fn の代わりに user を付けて呼ぶ */
  void *user;
};
/* host の関数の 1 つの引数か戻り値 (tvm_host の種類の列の 1 項を module の型で解いたもの。doc/vm-design.md §8.5) */
typedef struct TvmHostArg {
  char kind; /* 'i' 'f' 'b' 's' 'o'、struct は 'S' */
  bool
      byref; /* '&' (out): 引数の slot は値の場所を指す。呼び出しの後、値は CALLR の結果の register にある */
  const TvmType *st; /* struct の型 ('S'。葉は int / float / bool だけ) */
  uint32_t
      reg; /* 葉の先頭の register (値の引数は CALLR の a から、戻り値と out は b から) */
  uint32_t
      block; /* struct の塊の場所 (塊の領域の先頭からの byte。8 に揃える) */
} TvmHostArg;

/* struct か out を持つ host の関数の呼び方。
   呼び出しの間、VM の stack の今の frame の後ろに nslots の slot を取り、host に渡す引数の列 (nargs)、戻り値の slot (1)、struct の塊を並べる (入れ子の呼び出しはその後ろから積む)。 */
struct TvmHostPlan {
  uint32_t nargs;
  TvmHostArg *args;
  TvmHostArg ret; /* kind が 0 なら戻り値なし */
  uint32_t nslots;
};
/* 読み込み (layout の後): module が呼ぶ host の関数の呼び方を module の型で作る。合わなければ理由を tvm_error に置いて false */
bool tvm_host_plans(TvmVm *vm);
void tvm_host_plans_free(TvmHostPlan **plans, uint32_t n);
/* CALLR: plan で引数を写して host の関数を呼ぶ。ra / rb は CALLR の a / b の register */
TvmStatus tvm_host_call(TvmVm *vm, const TvmIntrinsic *in,
                        const TvmHostPlan *plan, TvmSlot *ra, TvmSlot *rb);

extern const TvmIntrinsic tvm_intrinsics[];
extern const size_t tvm_nintrinsics;
TvmStatus tvm_fault_msg(TvmVm *vm, const char *what);
int tvm_str_compare(const TvmString *a,
                    const TvmString *b); /* .NET の ordinal (-1 / 0 / 1) */
uint32_t tvm_kind_size(uint8_t kind);

/* tcsvm_run.c */
TvmStatus tvm_run(TvmVm *vm, const TvmProto *proto, const TvmSlot *args,
                  TvmSlot *rets);
/* 入れ子の呼び出し: closure を args で呼ぶ (runtime の List.Sort から) */
TvmStatus tvm_call_closure(TvmVm *vm, const TvmClosure *c, const TvmSlot *args,
                           TvmSlot *rets);
/* runtime の関数が delegate を呼ぶ。C の module の delegate (shared heap) は sig ("i:b" など) の invoke で呼ぶ */
TvmStatus tvm_call_delegate(TvmVm *vm, const TvmClosure *c, const char *sig,
                            const TvmSlot *args, TvmSlot *rets);
/* 種類と型の葉の sig の文字 (tvm_host と同じ。i / f / b / s / o。struct は '?') */
char tvm_sig_letter(const TvmVm *vm, uint8_t kind, uint16_t type);
TvmStatus tvm_run_nested(TvmVm *vm, const TvmProto *proto, const TvmSlot *args,
                         TvmSlot *rets);
/* 関数型 t の C の delegate の invoke (無ければ NULL) */
TvmDelegateFn tvm_delegate_for(TvmVm *vm, const TvmType *t);

/* tcsvm_rt.c の List.Sort */
TvmStatus tvm_list_sort(TvmVm *vm, TvmList *list, const TvmClosure *compare);

#endif
