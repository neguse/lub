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

void app_frame_garbage_push(App *app, void *ptr) {
  if (!ptr)
    return;
  if (app->frame_garbage_count >= app->frame_garbage_cap) {
    int cap = app->frame_garbage_cap ? app->frame_garbage_cap * 2 : 16;
    void **grown =
        (void **)realloc(app->frame_garbage, sizeof(void *) * (size_t)cap);
    if (!grown) {
      free(ptr); // 預かれなければその場で捨てる (view は既に壊れている)
      return;
    }
    app->frame_garbage = grown;
    app->frame_garbage_cap = cap;
  }
  app->frame_garbage[app->frame_garbage_count++] = ptr;
}
