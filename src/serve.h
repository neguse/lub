#ifndef LUB_SERVE_H
#define LUB_SERVE_H

#include "haxe_pipeline.h"
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

  HaxePipeline haxe;
  char hxml_path[768];
  char game_dir[768];
  char entry_name[128];

  ServeDataWatch data_watch;

  char wasm_dir[768];
  char slang_dir[768];
} ServeState;

bool serve_start(ServeState *s, const char *hxml_path, const char *wasm_dir,
                 const char *slang_dir, int port);
bool serve_tick(ServeState *s);
void serve_stop(ServeState *s);

#endif
