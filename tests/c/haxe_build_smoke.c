#include "../../src/haxe_build.h"
#include "../../src/haxe_server.h"
#include <SDL3/SDL.h>
#include <assert.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

int main(void) {
  // テスト用 sandbox を作る。
  SDL_CreateDirectory("/tmp/build_smoke");
  // 既存の生成物 (前回失敗の残骸) があれば掃除
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.lua");
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.raw.tmp");
  SDL_RemovePath("/tmp/build_smoke/.lub/Main.lua.tmp");

  FILE *fx = fopen("/tmp/build_smoke/Main.hx", "w");
  assert(fx);
  fprintf(fx, "class Main {\n"
              "  public static function main() {}\n"
              "  public static function onFrame() {}\n"
              "}\n");
  fclose(fx);

  FILE *fh = fopen("/tmp/build_smoke/Main.hxml", "w");
  assert(fh);
  fprintf(fh, "-cp /tmp/build_smoke\n-main Main\n");
  fclose(fh);

  HaxeServer s;
  if (!haxe_server_start(&s)) {
    SDL_Log("server start failed");
    return 1;
  }
  HxmlMeta m;
  if (!hxml_parse("/tmp/build_smoke/Main.hxml", &m)) {
    SDL_Log("hxml_parse failed");
    haxe_server_stop(&s);
    return 1;
  }
  HaxeBuildResult r = haxe_build_run(&s, "/tmp/build_smoke/Main.hxml", &m);
  haxe_server_stop(&s);
  if (!r.ok) {
    SDL_Log("build failed: %s", r.log);
    return 1;
  }

  // 生成された .lub/Main.lua を確認
  FILE *fc = fopen("/tmp/build_smoke/.lub/Main.lua", "rb");
  if (!fc) {
    SDL_Log("expected output Main.lua not found");
    return 1;
  }
  char buf[65536];
  size_t n = fread(buf, 1, sizeof(buf) - 1, fc);
  buf[n] = '\0';
  fclose(fc);

  // prelude marker
  if (!strstr(buf, "lua-utf8")) {
    SDL_Log("prelude marker 'lua-utf8' not found in output");
    return 1;
  }
  // namespace shim marker
  if (!strstr(buf, "lub.Gfx")) {
    SDL_Log("namespace shim 'lub.Gfx' not found in output");
    return 1;
  }
  // postlude marker
  if (!strstr(buf, "return Main")) {
    SDL_Log("postlude 'return Main' not found in output");
    return 1;
  }

  // raw.tmp が掃除されていることを確認
  FILE *raw = fopen("/tmp/build_smoke/.lub/Main.raw.tmp", "rb");
  if (raw) {
    fclose(raw);
    SDL_Log("raw.tmp was not cleaned up");
    return 1;
  }

  printf("OK\n");
  return 0;
}
