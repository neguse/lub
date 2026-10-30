#ifndef LUB_SERVE_H
#define LUB_SERVE_H

#include "tcs_build.h"
#include <SDL3/SDL_stdinc.h>
#include <stdbool.h>

#define SERVE_MAX_CONNS 32

typedef struct {
  char path[768];
  char web_path[512]; // ブラウザの MEMFS での path (cwd 基準)
  Sint64 mtime_ns;
} ServeWatchEntry;

typedef struct {
  ServeWatchEntry *entries;
  int count;
  int cap;
  Sint64 last_rescan_ns;
  bool pending;
} ServeDataWatch;

typedef struct {
  int fd;
  bool is_sse;
  char buf[4096];
  int buf_len;
} ServeConn;

typedef struct ServeState {
  int listen_fd;
  int port;

  ServeConn conns[SERVE_MAX_CONNS];
  int conn_count;

  TcsPipeline tcs;
  char entry_path[768]; // .csproj
  char game_dir[768];
  char entry_name[128];
  // ブラウザの MEMFS で game_dir を置く場所 (cwd 基準、"" か "/" 終わり)
  char web_dir[512];

  ServeDataWatch data_watch;

  char wasm_dir[768];
  char slang_dir[768];
} ServeState;

// entry_path は .csproj (entry class = basename、入力 = 同 dir 以下の *.cs)。
// tcs が transpile + watch し、生成 Lua (.lub/<Entry>.lua) と data file の名前を SSE でブラウザへ知らせる。
// 中身は GET /files/<path> で配る。
bool serve_start(ServeState *s, const char *entry_path, const char *wasm_dir,
                 const char *slang_dir, int port);
bool serve_tick(ServeState *s);
void serve_stop(ServeState *s);

// web_dir + game_dir からの相対 path を、ブラウザの MEMFS の path にする (区切りは /)。
void serve_web_path(const char *web_dir, const char *rel, char *out,
                    size_t cap);

#endif
