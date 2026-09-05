#include "digest.h"
#include "app.h"
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define FNV_OFFSET 1469598103934665603ULL
#define FNV_PRIME 1099511628211ULL

static void mix(App *app, const void *data, size_t len) {
  const uint8_t *p = (const uint8_t *)data;
  uint64_t h = app->digest.h ? app->digest.h : FNV_OFFSET;
  for (size_t i = 0; i < len; ++i) {
    h ^= p[i];
    h *= FNV_PRIME;
  }
  app->digest.h = h;
}

// LUB_DIGEST_TRACE=1 で項目ごとの値も出す (実行形の間で digest が割れた
// ときに、どの呼び出しから違うかを見る)。
static bool trace_enabled(void) {
  static int state = -1;
  if (state < 0) {
    const char *e = getenv("LUB_DIGEST_TRACE");
    state = e && *e && strcmp(e, "0") != 0;
  }
  return state == 1;
}

void digest_tag(App *app, const char *tag) {
  mix(app, tag, strlen(tag) + 1);
  if (trace_enabled())
    printf("\ntrace %s", tag);
}

void digest_str(App *app, LubStr s) {
  digest_i32(app, s.len);
  if (s.len > 0)
    mix(app, s.ptr, (size_t)s.len);
  if (trace_enabled())
    printf(" '%.*s'", (int)s.len, s.ptr ? s.ptr : "");
}

void digest_i32(App *app, int32_t v) {
  mix(app, &v, sizeof(v));
  if (trace_enabled())
    printf(" %d", (int)v);
}

void digest_frame_end(App *app) {
  if (!app->digest.enabled)
    return;
  if (trace_enabled())
    printf("\n");
  printf("digest %" PRIu64 " %016" PRIx64 "\n", app->frame_index,
         app->digest.h ? app->digest.h : FNV_OFFSET);
  fflush(stdout);
  app->digest.h = 0;
}
