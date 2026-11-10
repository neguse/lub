// src/tcs_host.c と link する偽の lub_host_*。
// SDL を使わず、決めた event 列を frame ごとに流す。
// QUIT を流した後は quit_requested を立てる (本物と同じ)。
// QUIT の後ろに置いた event は、host が受け取らずに終わるのでゲームに届かない。
// frame 1 の最初の frame_begin は描けない frame として false を返し、偽の SDL_Delay は待つ代わりに待った時間を出力する。
// frame_end は、失敗した frame を出力する。
// Quit の終了コードは process の終了コードになる。
#include <SDL3/SDL_timer.h>
#include <lub/lub_host.h>
#include <stdio.h>
#include <stdlib.h>

static const struct {
  int frame;
  LubEventData event;
} script[] = {
    {0, {.kind = LUB_EVENT_KIND_KEY_DOWN, .key = 4}},
    {0,
     {.kind = LUB_EVENT_KIND_MOUSE_BUTTON_DOWN,
      .button = 1,
      .x = 10.5f,
      .y = 20}},
    {1, {.kind = LUB_EVENT_KIND_MOUSE_WHEEL, .dy = -1}},
    {2, {.kind = LUB_EVENT_KIND_KEY_UP, .key = 4}},
    {2, {.kind = LUB_EVENT_KIND_QUIT}},
    {2, {.kind = LUB_EVENT_KIND_KEY_DOWN, .key = 99}},
};
static int frame, cursor;
static bool quit, not_ready_done;
static int32_t exit_code;

LubContext *lub_host_create(const LubHostOpts *opts) {
  (void)opts;
  return (LubContext *)malloc(1);
}
LubStatus lub_host_start(LubContext *ctx) {
  (void)ctx;
  return LUB_OK;
}
bool lub_host_poll_event(LubContext *ctx, LubEventData *out) {
  (void)ctx;
  if (cursor == (int)(sizeof script / sizeof *script) ||
      script[cursor].frame != frame)
    return false;
  *out = script[cursor++].event;
  if (out->kind == LUB_EVENT_KIND_QUIT)
    quit = true;
  return true;
}
bool lub_host_frame_begin(LubContext *ctx, float *dt) {
  (void)ctx;
  if (frame == 1 && !not_ready_done) {
    not_ready_done = true;
    return false;
  }
  *dt = 1.0f / 60;
  return true;
}
void SDL_Delay(Uint32 ms) { printf("delay %u\n", (unsigned)ms); }
void lub_host_frame_end(LubContext *ctx, bool frame_ok) {
  (void)ctx;
  if (!frame_ok)
    printf("frame failed\n");
  frame++;
}
bool lub_host_quit_requested(LubContext *ctx) {
  (void)ctx;
  return quit;
}
void lub_app_quit(LubContext *ctx, int32_t code) {
  (void)ctx;
  quit = true;
  exit_code = code;
}
int32_t lub_host_exit_code(LubContext *ctx) {
  (void)ctx;
  return exit_code;
}
void lub_host_destroy(LubContext *ctx) { free(ctx); }
