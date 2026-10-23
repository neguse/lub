// src/tcs_host.c と link する偽の lub_host_*。
// SDL を使わず、決めた event 列を frame ごとに流す。
// QUIT を流した後は quit_requested を立てる (本物と同じ)。
// QUIT の後ろに置いた event は、host が受け取らずに終わるのでゲームに届かない。
// frame 1 の最初の frame_begin は描けない frame として false を返し、偽の SDL_Delay は待つ代わりに待った時間を出力する。
#include <SDL3/SDL_timer.h>
#include <lub/lub_host.h>
#include <stdio.h>
#include <stdlib.h>

static const struct {
  int frame;
  LubEventData event;
} script[] = {
    {0, {LUB_EVENT_KIND_KEY_DOWN, 4, 0, 0, 0, 0, 0}},
    {0, {LUB_EVENT_KIND_MOUSE_BUTTON_DOWN, 0, 1, 10.5f, 20, 0, 0}},
    {1, {LUB_EVENT_KIND_MOUSE_WHEEL, 0, 0, 0, 0, 0, -1}},
    {2, {LUB_EVENT_KIND_KEY_UP, 4, 0, 0, 0, 0, 0}},
    {2, {LUB_EVENT_KIND_QUIT, 0, 0, 0, 0, 0, 0}},
    {2, {LUB_EVENT_KIND_KEY_DOWN, 99, 0, 0, 0, 0, 0}},
};
static int frame, cursor;
static bool quit, not_ready_done;

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
  (void)frame_ok;
  frame++;
}
bool lub_host_quit_requested(LubContext *ctx) {
  (void)ctx;
  return quit;
}
void lub_host_destroy(LubContext *ctx) { free(ctx); }
