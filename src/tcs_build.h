#pragma once
// tcs (TinyC#) pipeline: .cs entry の transpile + watch を lub が駆動する。
// 実装は tcs_build.c。
#include <SDL3/SDL.h>
#include <stdbool.h>

typedef struct TcsPipeline {
  SDL_Process *proc; // tcs --watch (kill at quit)
  bool enabled;
  // tcs の標準出力 (reload chunk の stream) のうち、まだ切り出していない分
  char *buf;
  size_t len, cap;
} TcsPipeline;

// cs_path (.csproj。entry class = basename、入力 = 同 dir の全 *.cs) を
// transpile して <dir>/.lub/<Base>.lua を生成し、tcs --watch を背後に張る。
// 成功時 out_lua に出力パスを書き true。初回 transpile 完了 (dotnet cold
// start 込み) まで block する。
bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz);
// 以後の rebuild ごとに tcs が書く reload chunk を、届いた順に 1 つ取り出す
// (block しない)。*out は SDL_malloc で確保し、呼び出し側が SDL_free する。
bool tcs_pipeline_next_chunk(TcsPipeline *p, char **out, size_t *out_len);
void tcs_pipeline_stop(TcsPipeline *p);

// cs-lib ディレクトリを解決して out に書く。優先順は LUB_CS_LIB 環境変数
// (指定したら fallback しない)、cwd の cs-lib、実行ファイルの 1 階層上の
// cs-lib。見つからなければ false。
bool tcs_resolve_cs_lib(char *out, size_t outsz);
