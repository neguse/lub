// frame ごとの digest。C API 呼び出しの構造 (関数名、key、個数、handle、
// version) を FNV-1a で畳み、frame の終わりに stdout へ出す (--digest)。
// 実行形 (tcs→Lua / .NET 実行 / tcs→C) の間で同じゲームが同じ列を出すことを
// CI が突き合わせる。float の値は混ぜない (実行形の間で float の差を許す)。
#pragma once
#include "lub/lub_api.h"
#include <stdbool.h>
#include <stdint.h>

struct App;

typedef struct DigestState {
  bool enabled;
  uint64_t h;
} DigestState;

void digest_tag(struct App *app, const char *tag);
void digest_str(struct App *app, LubStr s);
void digest_i32(struct App *app, int32_t v);
// frame の値を出して次の frame へ (app_frame_end が呼ぶ)。
void digest_frame_end(struct App *app);
