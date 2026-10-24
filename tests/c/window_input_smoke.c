// Input.Focused と Input.CaptureMouse を SDL の offscreen の window で確かめる。
// offscreen の window は入力のフォーカスを持ち、相対マウスモードには対応しない。
// CaptureMouse は適用できなかったことを false で返し、解除は通る。
#include "api_internal.h"
#include <lub/lub_host.h>
#include <stdio.h>

static int failures;

static void check(bool ok, const char *message) {
  if (!ok) {
    fprintf(stderr, "FAIL: %s (%s)\n", message, SDL_GetError());
    ++failures;
  }
}

int main(void) {
  SDL_SetHint(SDL_HINT_VIDEO_DRIVER, "offscreen");
  LubContext *ctx = lub_host_create(NULL);
  check(ctx != NULL, "lub_host_create");
  if (!ctx)
    return 1;
  SDL_Window *window = lub_api_app(ctx)->window;
  LubEventData event;
  while (lub_host_poll_event(ctx, &event)) {
  }
  check(lub_input_focused(ctx), "the offscreen window has input focus");
  check(!lub_input_capture_mouse(ctx, true) &&
            !SDL_GetWindowRelativeMouseMode(window),
        "capture reports that offscreen cannot apply it");
  check(lub_input_capture_mouse(ctx, false), "release is accepted");
  lub_host_destroy(ctx);
  if (!failures)
    puts("window input smoke: PASS");
  return failures ? 1 : 0;
}
