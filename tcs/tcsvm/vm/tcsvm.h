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
