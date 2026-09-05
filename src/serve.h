#ifndef LUB_SERVE_H
#define LUB_SERVE_H

#include "tcs_build.h"
#include <SDL3/SDL_stdinc.h>
#include <stdbool.h>

#define SERVE_MAX_CONNS 32

typedef struct {
  char path[768];
  char rel_path[512];
  Sint64 mtime_ns;
} ServeWatchEntry;

typedef struct {
  ServeWatchEntry *entries;
  int count;
  int cap;
  Sint64 last_change_ns;
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

  ServeDataWatch data_watch;

  char wasm_dir[768];
  char slang_dir[768];
} ServeState;

// entry_path は .csproj (entry class = basename、入力 = 同 dir の *.cs)。tcs が
// transpile + watch し、生成 Lua (.lub/<Entry>.lua) と data file の変更を SSE
// で ブラウザへ送る。
bool serve_start(ServeState *s, const char *entry_path, const char *wasm_dir,
                 const char *slang_dir, int port);
bool serve_tick(ServeState *s);
void serve_stop(ServeState *s);

#endif
