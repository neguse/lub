#pragma once
// Gfx.Pick: 指した画素に一番手前で描いた draw を引く。
// 要求の次の frame で、overlay の外 (ゲーム) の draw のうち指した target (main_tex か XR の眼) へのものを記録する。
// 呼び出し元は lua_ctx_call_sites (tcs の --trace があればその C# の行、無ければ Lua の行)。
// その frame の終わりに、記録した draw を同じ vertex shader と draw の番号を書く fragment shader で、指した 1 画素だけ流し直し、読み戻す。
// depth test と書き込みは元の draw のままなので、残るのは画面で一番手前に見える draw になる。
// 読み戻しが届いたら、draw の番号と、記録した呼び出し元 (tcs の --trace が置く C# の行の並び) を返す。
// 記録した draw が使う buffer と texture をその frame の間に作り直すか書き換えると、古い方を pick が預かって流し直しに使う。
// 同じ frame の後の compute が書き換えた storage buffer は、書き換えた後の中身で流し直す。
#include "backend.h"
#include <stdbool.h>
#include <stdint.h>

struct App;
struct ResEntry;

// target は -1 = main_tex、0 / 1 = XR の眼。
// 前の要求の結果は捨てる (記録中の frame で呼ぶと、その frame は前の要求のまま記録して結果を捨て、この要求は次の frame から記録する)
void pick_request(struct App *app, int target, int x, int y);

// frame の頭 (OnFrame の前) に呼ぶ。
// 要求があればこの frame を記録する
void pick_frame_begin(struct App *app);

// begin_pass のたびに呼ぶ。
// target は -1 = main_tex、0 / 1 = XR の眼、-2 = それ以外 (記録しない)
void pick_begin_pass(struct App *app, int target);

// draw を記録する (記録しない frame と overlay の draw は何もしない)。
// 続く pick_record_uniform は、この draw の uniform block
void pick_record_draw(struct App *app, struct ResEntry *sh,
                      const BindingsDesc *bind, int blend, int cull, int prim,
                      bool depth_test, bool depth_write, int count,
                      int instance_count);
void pick_record_uniform(struct App *app, int stage, int slot,
                         const float *data, size_t bytes);

// buffer か image を作り直すか書き換える前に呼ぶ。
// 記録中の draw が使っていれば pick が預かって (流し直した後に壊す) true を返し、呼び出し側は壊さず書き換えず新しく作る
bool pick_take_buffer(struct App *app, BackendBuffer buf);
bool pick_take_image(struct App *app, BackendImage image);

// frame の終わり (pass の外、backend の end_frame の前) に呼ぶ。
// 記録した frame なら流し直して読み戻しを要求し、待っている読み戻しがあれば受け取る
void pick_frame_end(struct App *app);

// 結果が届いていれば true。
// draw は frame の中の draw の番号 (0 始まり、無ければ -1)、site は呼び出し元 (改行区切り、内側から)。
// site は次の pick_request まで有効
bool pick_result(struct App *app, int *draw, const char **site);

void pick_shutdown(struct App *app);
