#pragma once
// Gfx.Pick: 指した画素に一番手前で描いた draw を引く。
// 要求の次の frame で、その app の draw のうち指した target (main_tex か XR の眼) へのものを記録する。
// 呼び出し元は player が pick_set_sites で立てた関数で、draw のたびにその場の呼び出しの並びを書かせる。
// その frame の終わりに、記録した draw を同じ vertex shader と draw の番号を書く fragment shader で、指した 1 画素だけ流し直し、読み戻す。
// depth test と書き込みは元の draw のままなので、残るのは画面で一番手前に見える draw になる。
// 読み戻しが届いたら、draw の番号と、その draw で記録した呼び出し元を返す。
// 記録した draw が使う buffer と texture をその frame の間に作り直すか書き換えると、古い方を pick が預かって流し直しに使う。
// 同じ frame の後の compute が書き換えた storage buffer は、書き換えた後の中身で流し直す。
#include "backend.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

struct App;
struct ResEntry;

// draw の呼び出し元を書く関数 (PickHit.Site の中身)。
// pick が記録する draw の中 (lub_gfx_draw_items の中) で呼ぶので、player はその場の呼び出しの並びを書く。
// out は cap バイトの領域で、NUL 終端の文字列を書く。
// 1 行に 1 つ "file:line" を、内側 (draw を呼んだ所) から外側へ改行で区切って並べる。
// その段に self の個体があれば、続けて "\t" と Id を書いてよい (PickHit.Self になる)。
// 入りきらない分は切ってよい。
// 何も書かなければ (out[0] が NUL のまま) Site は空になる。
typedef void (*PickSitesFn)(void *user, char *out, size_t cap);

// 呼び出し元を書く関数を立てる (fn が NULL なら外し、Site は空になる)。
// player が lub_host_create の後に立てる。
// tcs VM の player (src/vm_player.c) は VM の呼び出しの並びを書く関数を立てる。
void pick_set_sites(struct App *app, PickSitesFn fn, void *user);

// target は -1 = main_tex、0 / 1 = XR の眼。
// 前の要求の結果は捨てる (記録中の frame で呼ぶと、その frame は前の要求のまま記録して結果を捨て、この要求は次の frame から記録する)
void pick_request(struct App *app, int target, int x, int y);

// frame の頭 (OnFrame の前) に呼ぶ。
// 要求があればこの frame を記録する
void pick_frame_begin(struct App *app);

// begin_pass のたびに呼ぶ。
// target は -1 = main_tex、0 / 1 = XR の眼、-2 = それ以外 (記録しない)
void pick_begin_pass(struct App *app, int target);

// draw を記録する (記録しない frame は何もしない)。
// 続く pick_record_uniform は、この draw の uniform block
void pick_record_draw(struct App *app, struct ResEntry *sh,
                      const BindingsDesc *bind, int cull, int prim,
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
// draw は frame の中の draw の番号 (0 始まり、無ければ -1)、site は呼び出し元 (改行区切り、内側から)、
// self は site と同じ並びの段ごとの self の Id (無い段は空の行。どの段にも無ければ空)。
// site と self は次の pick_result か pick_request まで有効
bool pick_result(struct App *app, int *draw, const char **site,
                 const char **self);

void pick_shutdown(struct App *app);
