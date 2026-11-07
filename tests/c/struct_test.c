// tests/c/struct_stub.cs の関数の偽の C API。
// clang-format off
#include "game.c"
#include "binding.c"
// clang-format on
#include <assert.h>

const char *lub_last_error(LubContext *ctx) {
  (void)ctx;
  return "failure";
}
int32_t lub_frame_index(LubContext *ctx) {
  (void)ctx;
  return 1;
}
// flag は値で来る。opts は record で、Flag は has と値、Hit は値の field
LubStatus lub_structtest_make(LubContext *ctx, LubTestFlag flag,
                              const LubTestOpts *opts, LubTestHit *out) {
  (void)ctx;
  assert(flag.op == LUB_GFX_BLEND_OP_MAX);
  if (!opts)
    return LUB_NOT_FOUND;
  assert(!flag.on);
  assert(opts->has_flag && opts->flag.on &&
         opts->flag.op == LUB_GFX_BLEND_OP_MAX);
  assert(opts->hit.id == 3 && opts->hit.t == .5f && opts->hit.near &&
         opts->hit.at.x == 7);
  *out = opts->hit;
  out->id += 10;
  out->at.y += 1;
  return LUB_OK;
}
bool lub_structtest_find(LubContext *ctx, int32_t key, LubTestHit *hit,
                         LubTestFlag *flag) {
  (void)ctx;
  if (key < 0)
    return false;
  *hit = (LubTestHit){key * 2, 1.5f, true, {0, -4}};
  *flag = (LubTestFlag){true, LUB_GFX_BLEND_OP_SUBTRACT};
  return true;
}
void lub_structtest_flip(LubContext *ctx, LubTestFlag flag, LubTestPair pair,
                         LubTestFlag *out) {
  (void)ctx;
  assert(pair.a == 0 && pair.b == 0);
  *out = (LubTestFlag){!flag.on, LUB_GFX_BLEND_OP_MIN};
}
