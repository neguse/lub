#pragma once
// Rewind: overlay がゲームの時間を止め、過去の frame に戻る。
// 記録中は、ゲームの OnFrame の前ごとに、ゲームの状態と、その frame の入力と dt を控える (直近 capacity 個)。
// ゲームの状態は player が控える (LubDebugHooks の snapshot_*。tcs VM の player は entry と --trace の class の static から辿れる object の中身)。
// 戻すときは同じ object に中身を書き戻すので、object の同一性は変わらない。
// 止めている間は、毎 frame、控えた位置の状態に戻し、同じ入力と dt でゲームの OnFrame を呼び直して描く (今のコードで作り直す)。
// 再開すると、その位置の状態に戻し、それより後の控えを捨てて、そこから続ける。
// 物理の world、音、file など VM の外の状態は戻さない (止めている間の音は鳴らさない)。
// player が控えを戻せなくなったら (reload が object を作り直した)、控えを捨てる。
// 止めていたなら止めたままで、次の frame の状態を控え直してそこで止める (それより前へは戻れない)。
#include "app.h"
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

struct App;

// frames 個まで控える (0 で控えるのをやめ、控えを捨てる)
void rewind_record(struct App *app, int frames);
int rewind_count(const struct App *app);
// back frame 前 (0 = 直近に控えた frame) で止める。
// -1 で、止めている位置から続ける
void rewind_pause(struct App *app, int back);
// 止めている位置 (止めていなければ -1)
int rewind_position(const struct App *app);

// 今の位置 (止めていれば止めている frame、それ以外は直近) から back 個前の控え (player の snapshot)。
// 無ければ NULL
void *rewind_snapshot(const struct App *app, int back);

// ゲームの OnFrame の前に呼ぶ。
// 止めていれば状態を戻して、ゲームに渡す dt を *dt に書き、その frame の入力を差し替えて true。
// 止めていなければ (記録中なら控えて) false
bool rewind_frame_begin(struct App *app, double *dt);
// ゲームの OnFrame の後に呼ぶ (差し替えた入力を外す)
void rewind_frame_end(struct App *app);
// 止めているか (ゲームの OnEvent を呼ばない)
bool rewind_paused(const struct App *app);

// ゲームから読む入力 (止めている間に、ゲームの callback の中から読むと控えた入力、それ以外は NULL)。
// 控えるのは、その frame にゲームから見えたもの (overlay が Input.Grab で取っていた種類は何も押されていない)。
// 止めている間は今の Input.Grab によらずこれを返す (overlay は今の入力を読む)
const InputState *rewind_input(const struct App *app);

// 控えを全部捨てる (player が VM を作り直す前)
void rewind_drop(struct App *app);
void rewind_shutdown(struct App *app);
