#include "api_internal.h"
#include <SDL3/SDL.h>
#include <stdarg.h>
#include <stdio.h>
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
