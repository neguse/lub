// lub の host API。runtime を動かす側 (lub の player、.NET 実行の host) が
// 使う pull 型の面で、ゲームに見える面 (lub_api.h) とは別。host は
//   create → ゲームの OnInit → start → { poll_event* → frame_begin →
//   ゲームの OnFrame → frame_end }* → ゲームの OnQuit → destroy
// の順に呼ぶ。main thread 限定。
#pragma once
#include "lub_api.h"

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LubHostOpts {
  // "d3d12" / "vulkan" / "metal" / "openxr" / "sdlgpu"。len 0 = 環境変数
  // LUB_BACKEND か既定。ゲームの Config が指定すればそちらが勝つ。
  LubStr backend;
  // > 0 なら毎 frame この dt を使う (テスト用)。0 = 実測。
  float fixed_dt;
  // len 0 = 無し。capture_frame 枚目の frame を PNG に書き、quit_requested
  // を立てる。
  LubStr capture_path;
  int32_t capture_frame;
  // 各 frame の digest (描画と resource 宣言の構造) を stdout に出す。
  bool digest;
  // readback の届く frame を保証するモードの既定 (ゲームの Config の
  // ReadbackFixedLatency が指定すればそちらが勝つ)。
  bool readback_fixed_latency;
} LubHostOpts;

// SDL と runtime を初期化して context を作る。失敗は NULL (log 済み)。
LUB_API LubContext *lub_host_create(const LubHostOpts *opts);

// ゲームの OnInit (Config) の後に呼ぶ。window size を反映して backend を
// 起動する。
LUB_API LubStatus lub_host_start(LubContext *ctx);

// 溜まった event を 1 件取り出す。無ければ false。終了要求は
// LUB_EVENT_KIND_QUIT で届き、以後 quit_requested が true になる。
LUB_API bool lub_host_poll_event(LubContext *ctx, LubEventData *out);

// frame を始めて dt (秒) を返す。false なら描けない frame (window が 0
// サイズ、または web でその frame が要る GPU の結果がまだ届いていない) で、
// host は少し待って次の frame へ進む。
LUB_API bool lub_host_frame_begin(LubContext *ctx, float *dt);

// frame を終える (ゲームの OnFrame の後)。frame_ok はゲームの OnFrame が
// error なく終わったか。false の frame は resource の sweep までの frame に
// 数えない。
LUB_API void lub_host_frame_end(LubContext *ctx, bool frame_ok);

// Quit が呼ばれたか、capture が終わったか、window が閉じられた。
LUB_API bool lub_host_quit_requested(LubContext *ctx);

// Quit に渡された終了コード (Quit が無ければ 0)。
LUB_API int32_t lub_host_exit_code(LubContext *ctx);

// runtime と SDL を終了して context を捨てる。env LUB_EXIT_FILE があれば、
// その file に終了コードを書く (終了コードを受け取れない iOS simulator 用)。
LUB_API void lub_host_destroy(LubContext *ctx);

#ifdef __cplusplus
}
#endif
