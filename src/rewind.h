#pragma once
// Rewind: overlay がゲームの時間を止め、過去の frame に戻る。
// 記録中は、ゲームの OnFrame の前ごとに、ゲームの状態と、その frame の入力と dt を控える (直近 capacity 個)。
// ゲームの状態は、entry の class と global の __tcs_trace_classes に並んだ class (overlay の class を除く) の table から辿れる table の中身 (関数を除く)。
// 戻すときは同じ table に中身を書き戻すので、table の同一性は変わらない。
// 止めている間は、毎 frame、控えた位置の状態に戻し、同じ入力と dt でゲームの OnFrame を呼び直して描く (今のコードで作り直す)。
// 再開すると、その位置の状態に戻し、それより後の控えを捨てて、そこから続ける。
// 物理の world、音、file など Lua の外の状態は戻さない (止めている間の音は鳴らさない)。
#include <SDL3/SDL.h>
#include <stdbool.h>
#include <stdint.h>

struct App;

// 控えた 1 frame の入力。
// 止めている間、ゲームから読む Input はこれを返す (overlay は今の入力を読む)
typedef struct RewindInput {
  bool keys[SDL_SCANCODE_COUNT];
  bool key_pressed[SDL_SCANCODE_COUNT];
  bool key_released[SDL_SCANCODE_COUNT];
  uint32_t mouse_buttons;
  uint32_t mouse_pressed_mask;
  uint32_t mouse_released_mask;
  float mouse_x, mouse_y;
  float mouse_rel_x, mouse_rel_y;
  float mouse_wheel_x, mouse_wheel_y;
} RewindInput;

// frames 個まで控える (0 で控えるのをやめ、控えを捨てる)
void rewind_record(struct App *app, int frames);
int rewind_count(const struct App *app);
// back frame 前 (0 = 直近に控えた frame) で止める。
// -1 で、止めている位置から続ける
void rewind_pause(struct App *app, int back);
// 止めている位置 (止めていなければ -1)
int rewind_position(const struct App *app);

// ゲームの OnFrame の前に呼ぶ。
// 止めていれば状態を戻して、ゲームに渡す dt を *dt に書き、その frame の入力を差し替えて true。
// 止めていなければ (記録中なら控えて) false
bool rewind_frame_begin(struct App *app, double *dt);
// ゲームの OnFrame の後に呼ぶ (差し替えた入力を外す)
void rewind_frame_end(struct App *app);
// 止めているか (ゲームの OnEvent を呼ばない)
bool rewind_paused(const struct App *app);

// ゲームから読む入力 (止めている間に、ゲームの callback の中から読むと控えた入力、それ以外は NULL)
const RewindInput *rewind_input(const struct App *app);

void rewind_shutdown(struct App *app);
