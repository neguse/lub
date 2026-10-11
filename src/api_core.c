#include "api_internal.h"
#include <SDL3/SDL.h>
#include <stdarg.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

LubStatus lub_api_fail(App *app, const char *fmt, ...) {
  va_list ap;
  va_start(ap, fmt);
  vsnprintf(app->last_error, sizeof(app->last_error), fmt, ap);
  va_end(ap);
  return LUB_ERROR;
}

const char *lub_last_error(LubContext *ctx) {
  return lub_api_app(ctx)->last_error;
}

int32_t lub_frame_index(LubContext *ctx) {
  return (int32_t)lub_api_app(ctx)->frame_index;
}

struct FrameGarbage {
  void *ptr;
  void (*free_fn)(void *);
};

void app_frame_garbage_push_fn(App *app, void *ptr, void (*free_fn)(void *)) {
  if (!ptr)
    return;
  if (app->frame_garbage_count >= app->frame_garbage_cap) {
    int cap = app->frame_garbage_cap ? app->frame_garbage_cap * 2 : 16;
    struct FrameGarbage *grown = (struct FrameGarbage *)realloc(
        app->frame_garbage, sizeof(*grown) * (size_t)cap);
    if (!grown) {
      free_fn(ptr); // 預かれなければその場で捨てる (view は既に壊れている)
      return;
    }
    app->frame_garbage = grown;
    app->frame_garbage_cap = cap;
  }
  struct FrameGarbage *g = &app->frame_garbage[app->frame_garbage_count++];
  g->ptr = ptr;
  g->free_fn = free_fn;
}

void app_frame_garbage_push(App *app, void *ptr) {
  app_frame_garbage_push_fn(app, ptr, free);
}

void app_frame_garbage_drain(App *app) {
  for (int i = 0; i < app->frame_garbage_count; ++i)
    app->frame_garbage[i].free_fn(app->frame_garbage[i].ptr);
  app->frame_garbage_count = 0;
}
