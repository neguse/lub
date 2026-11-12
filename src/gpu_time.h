#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

struct App;

// 負荷を数えている間の、pass ごとの GPU の時間と draw の数 (Sys.TakeGpuPasses)。
// pass.c が pass の始まりと終わりを、draw を出す所が draw を知らせる。
// 時刻を測れる backend は pass の前後に時刻を書き、その frame を GPU が終えた後に gpu_time_resolved で返す。
// 測れない backend の pass は、frame の終わりに時間 -1 で出す。
// Gfx.BeginPass で始めた pass を控え (overlay の中の pass と lub の中の pass は除く)、debug hook (tcs VM の player) が今の --trace の行を返せばそれも控える。
// frame の途中で数え始めたら、次の frame の pass から控える。

// 1 frame で控える pass の数の上限。backend の時刻の番号は pass i の前が 2i、後が 2i + 1
#define GPU_TIME_PASSES 64
#define GPU_TIME_STAMPS (GPU_TIME_PASSES * 2)
// 書かなかった時刻
#define GPU_TIME_NONE UINT64_MAX

void gpu_time_enable(struct App *app, bool on);
bool gpu_time_enabled(void);
// 始める pass を控えるか (pass.c が backend の begin_pass の前に呼び、PassBeginDesc.timed に入れる。pass を作るときに時刻を頼む backend が使う)
bool gpu_time_wants_pass(struct App *app);
// target は描き先の名前 (Gfx.BeginPass の外で始めた pass は NULL で、控えない)
void gpu_time_pass_begin(struct App *app, const char *target);
void gpu_time_pass_end(struct App *app);
void gpu_time_draw(struct App *app);
// backend の end_frame の前に呼ぶ
void gpu_time_frame_end(struct App *app);
// backend が frame の時刻 (ns、count 個) を返す。書かなかった時刻は GPU_TIME_NONE
void gpu_time_resolved(uint64_t frame, const uint64_t *ns, uint32_t count);
// 前に取り出してから届いた pass を古い順に渡す (次に呼ぶまで有効)
void gpu_time_take(const LubGpuPass **out, int32_t *count);

#ifdef __cplusplus
}
#endif
