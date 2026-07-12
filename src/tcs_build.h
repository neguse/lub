#pragma once
// tcs (TinyC#) pipeline: .cs entry の transpile + watch を lub が駆動する。
// hxml (haxe_pipeline) と対称の DX。実装は tcs_build.c。
#include <SDL3/SDL.h>
#include <stdbool.h>

typedef struct TcsPipeline {
  SDL_Process *proc; // tcs --watch (kill at quit)
  bool enabled;
} TcsPipeline;

// cs_path (.csproj。entry class = basename、入力 = 同 dir の全 *.cs) を
// transpile して <dir>/.lub/<Base>.lua を生成し、tcs --watch を背後に張る。
// 成功時 out_lua に出力パスを書き true。初回 transpile 完了 (dotnet cold
// start 込み) まで block する。
bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz);
void tcs_pipeline_stop(TcsPipeline *p);
