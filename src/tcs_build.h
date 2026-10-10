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

// cs_path (.csproj。entry class = basename、入力 = 同 dir 以下の全 *.cs) を
// transpile して <dir>/.lub/<Base>.lua を生成し、tcs --watch を背後に張る。
// 成功時 out_lua に出力パスを書き true。初回 transpile 完了 (dotnet cold
// start 込み) まで block する。
bool tcs_pipeline_start(TcsPipeline *p, const char *cs_path, char *out_lua,
                        size_t out_lua_sz);
// 以後の rebuild ごとに tcs が書く reload chunk を、届いた順に 1 つ取り出す
// (block しない)。*out は SDL_malloc で確保し、呼び出し側が SDL_free する。
bool tcs_pipeline_next_chunk(TcsPipeline *p, char **out, size_t *out_len);
void tcs_pipeline_stop(TcsPipeline *p);

// csproj ディレクトリ dir の .cs 一覧 (dir からの相対 path、'/' 区切り、
// 昇順)。SDK-style csproj の implicit glob (**/*.cs) に倣い、サブディレクトリ
// も辿る。bin / obj と、名前が '.' で始まるディレクトリ (.lub を含む) は
// 辿らない。*count に個数を書く。tcs_free_sources で解放する。
char **tcs_glob_sources(const char *dir, int *count);
void tcs_free_sources(char **sources, int count);
