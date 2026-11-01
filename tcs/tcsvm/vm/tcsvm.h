/* tcs VM — TinyC# の IL から作った bytecode (.tcsb) を実行する VM。
   設計は doc/vm-design.md。V1 の核に V2 の一部 (string / List / Dictionary / closure /
   instance method) を足した版 (同書 §17)。 */
#ifndef TCSVM_H
#define TCSVM_H

#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef union TvmSlot {
  int32_t i;
  float f;
  void *p;
  uint64_t u;
} TvmSlot;

typedef enum TvmStatus {
  TVM_OK = 0,
  TVM_LOAD_ERROR = 1,
  TVM_FAULT = 2,
  TVM_RESTART = 3, /* tvm_apply: 当てられない差分 (理由は tvm_error)。host は restart の導線へ */
} TvmStatus;

typedef struct TvmVm TvmVm;

typedef struct TvmConfig {
  size_t stack_slots; /* 0 は既定 (1M slot) */
  size_t max_frames;  /* 0 は既定 (64K) */
  int gc_stress;      /* 0 以外: 確保の地点とフレーム境界ごとに回収する (検査用) */
} TvmConfig;

TvmVm *tvm_new(const TvmConfig *config);
void tvm_free(TvmVm *vm);

/* bytecode を読み込み、検査し、link して static initializer を走らせる。
   失敗したら TVM_LOAD_ERROR か TVM_FAULT を返し、tvm_error() が理由を返す。 */
TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size);

/* hot reload (doc/vm-design.md §12、doc/object-model.md §6): 新版の module と計画 (tcs2vm --reload-from が作る)
   を当てる。フレーム境界 (VM の frame が無いとき) でだけ受け付ける。
   TVM_LOAD_ERROR / TVM_RESTART は当てていない (状態は旧版のまま。理由は tvm_error)。
   TVM_OK のとき tvm_error() が空でなければ、initializer か OnReload の fault の警告 (反映は巻き戻さない)。 */
TvmStatus tvm_apply(TvmVm *vm, const uint8_t *module, size_t size, const uint8_t *plan, size_t plan_size);

/* ---- shared heap (doc/object-model.md §10.2) ----
   C (tcs2c) の module と 1 つの heap と GC を共有する。tcs2c --lib --vm-host の glue の tcs_vm_register が入れる
   (host が直接呼ぶことは無い)。共有すると、VM の object は tcs2c の GC が辿り (kind FOREIGN)、回収は frame の境界
   (tcs_lib_gc / tvm_frame_end) だけで起き、C の module が VM の object を static などに持てる。1 process に 1 つの VM。 */
typedef void (*TvmSlotFn)(void **slot);
typedef struct TvmHeap {
  void *(*alloc)(uint32_t kind, size_t size); /* 0 で埋めた payload */
  void (*enter)(void);                        /* VM の呼び出しに入る (回収を止める) */
  void (*leave)(void);
  void (*barrier)(const void *owner);         /* owner に参照を書いた (write barrier) */
  void (*frame)(void);                        /* frame の境界の回収 */
  /* 全部の object の参照の場所と C の module の root を fn に渡す (reload の張り替え。境界で呼ぶ) */
  void (*visit_all)(TvmSlotFn fn);
  /* 旧世代の object の payload を順に渡す (境界の後なので若い object は無い) */
  void (*each)(void (*fn)(void *payload, uint32_t kind, void *ctx), void *ctx);
} TvmHeap;
void tvm_use_heap(TvmVm *vm, const TvmHeap *heap); /* tvm_load の前 */
/* runtime/core の GC が呼ぶ: VM の root と、VM の object の参照の場所 */
void tvm_heap_roots(TvmSlotFn fn);
void tvm_heap_refs(void *payload, TvmSlotFn fn);

/* tcs2vm --watch の stream の vm_batch (base64 を戻した byte 列: u32 の module の長さ、module、u32 の計画の長さ、
   計画) を当てる。戻り値は tvm_apply と同じ */
TvmStatus tvm_apply_batch(TvmVm *vm, const uint8_t *batch, size_t size);

/* ---- host (doc/vm-design.md §8.5、§13) ----
   host の関数は tvm_load の前に名前で登録する。名前は IL の host の名前 (--ref の stub の `Eng.Gfx.Begin` なら
   "eng.gfx.begin"、static の値 `Gfx.MainTex` は引数の無い "eng.gfx.main_tex")。params / rets は葉の種類の列:
   i int (enum を含む)、f float、b bool、s string、o ほかの参照 (host の data class の object、List など)。
   struct の値は葉に展開する。module が使う host の関数が無いか、呼び出しの形が合わなければ読み込みエラー。
   host の関数は args の値を読み、rets に書く。失敗は tvm_host_fault の戻り値を返す (C# の trace が付く)。
   host の関数の中では回収は起きない (回収は interpreter の地点とフレーム境界だけ)。 */
typedef TvmStatus (*TvmHostFn)(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user);
int tvm_host(TvmVm *vm, const char *name, const char *params, const char *rets, TvmHostFn fn, void *user);
TvmStatus tvm_host_fault(TvmVm *vm, const char *message);

/* host が VM の object を読み書きする。object の payload は型記述子の layout (doc/object-model.md §5) で、
   field の offset は名前で引く (IL の名前。stub の field `Title` なら "title")。 */
int32_t tvm_field_offset(TvmVm *vm, const char *type, const char *field); /* 無ければ -1 */
void *tvm_object_new(TvmVm *vm, const char *type);                        /* field は 0。型が無ければ NULL */
const char *tvm_object_type(const void *object);                           /* 型の名前 */
void *tvm_string_new(TvmVm *vm, const char *data, size_t len);
const char *tvm_string_data(const void *string, size_t *len);

/* handle: host (C の module) が frame を跨いで VM の object を持つ (doc/object-model.md §10)。GC の root になり、
   reload で object が作り直されたら handle は新しい object を指す。番号は 1 から (0 は無効)。 */
/* VM の closure (delegate) を host が呼ぶ (X-4)。args / rets は関数型の葉。VM の呼び出しの中 (host の関数から) でも外でも呼べる */
TvmStatus tvm_invoke(TvmVm *vm, void *closure, const TvmSlot *args, size_t nargs, TvmSlot *rets, size_t nrets);

/* VM の object の instance method を名前で呼ぶ (X-4: C の module の interface を VM の class が実装したとき、C の
   dispatcher が呼ぶ)。itable から名前と引数の数で引く。args / rets は self を除いた葉 */
TvmStatus tvm_invoke_method(TvmVm *vm, void *object, const char *method, const TvmSlot *args, size_t nargs,
                            TvmSlot *rets, size_t nrets);

/* C の module の delegate を VM が呼ぶ (X-4)。sig は引数の葉の種類の文字、':'、戻り値の種類の文字 ("i:b"、"s:" など。
   tvm_host と同じ文字)。CALLD が C の closure (VM の object でない delegate) に会うと、関数型の sig の invoke を呼ぶ */
typedef TvmStatus (*TvmDelegateFn)(void *closure, const TvmSlot *args, TvmSlot *rets);
int tvm_host_delegate(TvmVm *vm, const char *sig, TvmDelegateFn fn);

typedef uint32_t TvmHandle;
TvmHandle tvm_hold(TvmVm *vm, void *object);
void *tvm_handle_get(TvmVm *vm, TvmHandle h);
void tvm_release(TvmVm *vm, TvmHandle h);

/* 関数を名前で引く ("Particles.Main")。無ければ -1。 */
int32_t tvm_find(TvmVm *vm, const char *name);
/* module が entry と記した関数 (無ければ -1)。 */
int32_t tvm_entry(TvmVm *vm);

/* 引数は args[0..nargs)、戻り値は rets[0..nrets) に書く。
   nargs が関数の引数の slot 数と違うか、nrets が戻り値の slot 数より小さければ TVM_LOAD_ERROR。 */
TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs, TvmSlot *rets,
                   size_t nrets);

/* フレーム境界 (host が entry から戻った後)。溜まった確保が閾値を超えていれば回収する。 */
void tvm_frame_end(TvmVm *vm);
/* 直前の回収の後に生きていた byte 数 */
size_t tvm_gc_live(TvmVm *vm);

/* 最後のエラーか fault の message (C# の stack trace を含む)。 */
const char *tvm_error(TvmVm *vm);

#ifdef __cplusplus
}
#endif

#endif
