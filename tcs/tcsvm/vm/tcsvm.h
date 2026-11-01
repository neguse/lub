/* tcs VM — TinyC# の IL から作った bytecode (.tcsb) を実行する VM。
   設計は doc/vm-design.md。この版は V1 (同書 §17) の範囲: i32 / f32 / bool / class /
   struct の配列要素 / 配列 / static field / static method / 制御。 */
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
} TvmStatus;

typedef struct TvmVm TvmVm;

typedef struct TvmConfig {
  size_t stack_slots; /* 0 は既定 (1M slot) */
  size_t max_frames;  /* 0 は既定 (64K) */
} TvmConfig;

TvmVm *tvm_new(const TvmConfig *config);
void tvm_free(TvmVm *vm);

/* bytecode を読み込み、検査し、link して static initializer を走らせる。
   失敗したら TVM_LOAD_ERROR か TVM_FAULT を返し、tvm_error() が理由を返す。 */
TvmStatus tvm_load(TvmVm *vm, const uint8_t *bytes, size_t size);

/* 関数を名前で引く ("Particles.Main")。無ければ -1。 */
int32_t tvm_find(TvmVm *vm, const char *name);
/* module が entry と記した関数 (無ければ -1)。 */
int32_t tvm_entry(TvmVm *vm);

/* 引数は args[0..nargs)、戻り値は rets[0..nrets) に書く。
   nargs が関数の引数の slot 数と違うか、nrets が戻り値の slot 数より小さければ TVM_LOAD_ERROR。 */
TvmStatus tvm_call(TvmVm *vm, int32_t func, const TvmSlot *args, size_t nargs, TvmSlot *rets,
                   size_t nrets);

/* 最後のエラーか fault の message (C# の stack trace を含む)。 */
const char *tvm_error(TvmVm *vm);

#ifdef __cplusplus
}
#endif

#endif
