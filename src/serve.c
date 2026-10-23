#include "serve.h"
#include "embedded_serve_page.h"
#include "path_util.h"
#include "sock_compat.h"
#include <SDL3/SDL.h>
#include <errno.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#define WATCH_DEBOUNCE_NS (50LL * 1000LL * 1000LL)

// ---- JSON helpers -----------------------------------------------------------

static size_t json_escape(const char *in, size_t in_len, char *out,
                          size_t out_cap) {
  size_t w = 0;
#define PUTC(c)                                                                \
  do {                                                                         \
    if (w < out_cap)                                                           \
      out[w] = (c);                                                            \
    w++;                                                                       \
  } while (0)
  for (size_t i = 0; i < in_len; i++) {
    unsigned char c = (unsigned char)in[i];
    switch (c) {
    case '"':
      PUTC('\\');
      PUTC('"');
      break;
    case '\\':
      PUTC('\\');
      PUTC('\\');
      break;
    case '\n':
      PUTC('\\');
      PUTC('n');
      break;
    case '\r':
      PUTC('\\');
      PUTC('r');
      break;
    case '\t':
      PUTC('\\');
      PUTC('t');
      break;
    default:
      if (c < 0x20) {
        char esc[7];
        SDL_snprintf(esc, sizeof(esc), "\\u%04x", c);
        for (int j = 0; esc[j]; j++)
          PUTC(esc[j]);
      } else {
        PUTC((char)c);
      }
      break;
    }
  }
#undef PUTC
  if (w < out_cap)
    out[w] = '\0';
  return w;
}

// ---- socket helpers ---------------------------------------------------------

static int create_listen_socket(int port) {
  int fd = (int)socket(AF_INET, SOCK_STREAM, 0);
  if (fd < 0)
    return -1;
  int opt = 1;
  setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, (const char *)&opt, sizeof(opt));
  struct sockaddr_in addr = {0};
  addr.sin_family = AF_INET;
  addr.sin_addr.s_addr = htonl(INADDR_ANY);
  addr.sin_port = htons((uint16_t)port);
  if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
    sock_close(fd);
    return -1;
  }
  if (listen(fd, 8) < 0) {
    sock_close(fd);
    return -1;
  }
  sock_set_nonblocking(fd);
  return fd;
}

// ---- HTTP response helpers --------------------------------------------------

static const char *content_type_for(const char *path) {
  size_t n = strlen(path);
  if (n > 5 && strcmp(path + n - 5, ".html") == 0)
    return "text/html";
  if (n > 3 && strcmp(path + n - 3, ".js") == 0)
    return "application/javascript";
  if (n > 5 && strcmp(path + n - 5, ".wasm") == 0)
    return "application/wasm";
  if (n > 5 && strcmp(path + n - 5, ".data") == 0)
    return "application/octet-stream";
  if (n > 4 && strcmp(path + n - 4, ".css") == 0)
    return "text/css";
  return "application/octet-stream";
}

static bool send_all(int fd, const char *data, size_t len) {
  size_t sent = 0;
  while (sent < len) {
    ssize_t n = send(fd, data + sent, (int)(len - sent), MSG_NOSIGNAL);
    if (n < 0) {
      if (sock_would_block())
        continue;
      return false;
    }
    sent += (size_t)n;
  }
  return true;
}

static bool send_http_response(int fd, int status, const char *status_text,
                               const char *content_type, const char *body,
                               size_t body_len) {
  char hdr[512];
  int hdr_len = SDL_snprintf(hdr, sizeof(hdr),
                             "HTTP/1.1 %d %s\r\n"
                             "Content-Type: %s\r\n"
                             "Content-Length: %zu\r\n"
                             "Access-Control-Allow-Origin: *\r\n"
                             "Connection: close\r\n"
                             "\r\n",
                             status, status_text, content_type, body_len);
  if (!send_all(fd, hdr, (size_t)hdr_len))
    return false;
  if (body_len > 0 && body)
    return send_all(fd, body, body_len);
  return true;
}

static bool send_file_response(int fd, const char *file_path) {
  SDL_IOStream *io = SDL_IOFromFile(file_path, "rb");
  if (!io)
    return false;
  Sint64 sz = SDL_GetIOSize(io);
  if (sz < 0) {
    SDL_CloseIO(io);
    return false;
  }
  bool ok = send_http_response(fd, 200, "OK", content_type_for(file_path), NULL,
                               (size_t)sz);
  char chunk[64 * 1024];
  for (Sint64 left = sz; ok && left > 0;) {
    size_t n = SDL_ReadIO(io, chunk, sizeof(chunk));
    if (n == 0)
      break;
    ok = send_all(fd, chunk, n);
    left -= (Sint64)n;
  }
  SDL_CloseIO(io);
  return ok;
}

static bool send_sse_headers(int fd) {
  const char *hdr = "HTTP/1.1 200 OK\r\n"
                    "Content-Type: text/event-stream\r\n"
                    "Cache-Control: no-cache\r\n"
                    "Access-Control-Allow-Origin: *\r\n"
                    "Connection: keep-alive\r\n"
                    "\r\n";
  return send_all(fd, hdr, strlen(hdr));
}

// ---- data file watcher ------------------------------------------------------

static void watch_add(ServeDataWatch *w, const char *full_path,
                      const char *web_path) {
  if (w->count == w->cap) {
    int new_cap = w->cap ? w->cap * 2 : 32;
    ServeWatchEntry *grown = (ServeWatchEntry *)SDL_realloc(
        w->entries, (size_t)new_cap * sizeof(ServeWatchEntry));
    if (!grown)
      return;
    w->entries = grown;
    w->cap = new_cap;
  }
  SDL_PathInfo info;
  Sint64 mt = 0;
  if (SDL_GetPathInfo(full_path, &info))
    mt = (Sint64)info.modify_time;
  SDL_strlcpy(w->entries[w->count].path, full_path, sizeof(w->entries[0].path));
  SDL_strlcpy(w->entries[w->count].web_path, web_path,
              sizeof(w->entries[0].web_path));
  w->entries[w->count].mtime_ns = mt;
  w->count++;
}

typedef struct {
  ServeDataWatch *watch;
  const char *base_dir;
  size_t base_len;
  const char *web_dir;
} WatchEnumCtx;

static SDL_EnumerationResult SDLCALL watch_enum_cb(void *userdata,
                                                   const char *dir,
                                                   const char *fname) {
  WatchEnumCtx *ctx = (WatchEnumCtx *)userdata;
  if (!fname || fname[0] == '.')
    return SDL_ENUM_CONTINUE;

  // SDL は dir を区切り付きで渡す
  char full[768];
  size_t dn = strlen(dir);
  bool sep = dn > 0 && (dir[dn - 1] == '/' || dir[dn - 1] == '\\');
  SDL_snprintf(full, sizeof(full), "%s%s%s", dir, sep ? "" : "/", fname);

  SDL_PathInfo info;
  if (!SDL_GetPathInfo(full, &info))
    return SDL_ENUM_CONTINUE;

  if (info.type == SDL_PATHTYPE_DIRECTORY) {
    SDL_EnumerateDirectory(full, watch_enum_cb, userdata);
  } else if (info.type == SDL_PATHTYPE_FILE) {
    size_t n = strlen(fname);
    // C# のソースは tcs が読む (ブラウザには生成 Lua を送る)
    if (n > 3 && SDL_strcasecmp(fname + n - 3, ".cs") == 0)
      return SDL_ENUM_CONTINUE;
    if (n > 7 && SDL_strcasecmp(fname + n - 7, ".csproj") == 0)
      return SDL_ENUM_CONTINUE;

    const char *rel = full + ctx->base_len;
    if (*rel == '/')
      rel++;
    char web[512];
    SDL_snprintf(web, sizeof(web), "%s%s", ctx->web_dir, rel);
    watch_add(ctx->watch, full, web);
  }
  return SDL_ENUM_CONTINUE;
}

static bool data_watch_init(ServeDataWatch *w, const char *game_dir,
                            const char *web_dir) {
  SDL_zerop(w);
  WatchEnumCtx ctx = {w, game_dir, strlen(game_dir), web_dir};
  SDL_EnumerateDirectory(game_dir, watch_enum_cb, &ctx);
  SDL_Log("[serve] watching %d data files in %s", w->count, game_dir);
  return true;
}

static void data_watch_shutdown(ServeDataWatch *w) {
  SDL_free(w->entries);
  SDL_zerop(w);
}

// Returns a bitmask-like count of changed entries. Sets changed_indices array.
// Caller provides changed_indices with capacity >= w->count.
static int data_watch_tick(ServeDataWatch *w, int *changed_indices) {
  if (!w || w->count == 0)
    return 0;
  int changed = 0;
  Sint64 now = (Sint64)SDL_GetTicksNS();
  bool any_change = false;
  for (int i = 0; i < w->count; i++) {
    SDL_PathInfo info;
    Sint64 mt = 0;
    if (SDL_GetPathInfo(w->entries[i].path, &info))
      mt = (Sint64)info.modify_time;
    if (mt != w->entries[i].mtime_ns) {
      w->entries[i].mtime_ns = mt;
      any_change = true;
    }
  }
  if (any_change) {
    w->last_change_ns = now;
    w->pending = true;
  }
  if (w->pending && now - w->last_change_ns >= WATCH_DEBOUNCE_NS) {
    w->pending = false;
    // Collect all entries as "changed" after debounce
    // (we don't track per-entry change flags, so re-check all)
    for (int i = 0; i < w->count; i++) {
      changed_indices[changed++] = i;
    }
  }
  return changed;
}

// Re-scan directory for new files
static void data_watch_rescan(ServeDataWatch *w, const char *game_dir,
                              const char *web_dir) {
  // Save old entries to detect truly new files
  int old_count = w->count;
  ServeWatchEntry *old = w->entries;
  w->entries = NULL;
  w->count = 0;
  w->cap = 0;

  WatchEnumCtx ctx = {w, game_dir, strlen(game_dir), web_dir};
  SDL_EnumerateDirectory(game_dir, watch_enum_cb, &ctx);

  // Restore mtimes for paths that existed before so they don't trigger
  for (int i = 0; i < w->count; i++) {
    for (int j = 0; j < old_count; j++) {
      if (strcmp(w->entries[i].path, old[j].path) == 0) {
        w->entries[i].mtime_ns = old[j].mtime_ns;
        break;
      }
    }
  }
  SDL_free(old);
}

// ---- SSE message builder ----------------------------------------------------

// 生成 Lua (.lub/<Entry>.lua) の disk の path とブラウザでの path。
// .lub は data watch の外 (dot dir) なので別に持つ。
static void entry_lua_paths(const ServeState *s, char *disk, size_t disk_cap,
                            char *web, size_t web_cap) {
  SDL_snprintf(disk, disk_cap, "%s/.lub/%s.lua", s->game_dir, s->entry_name);
  SDL_snprintf(web, web_cap, "%s.lub/%s.lua", s->web_dir, s->entry_name);
}

static bool buf_reserve(char **buf, size_t *cap, size_t need) {
  if (need <= *cap)
    return true;
  char *grown = (char *)realloc(*buf, need * 2);
  if (!grown)
    return false;
  *buf = grown;
  *cap = need * 2;
  return true;
}

// "str" を足す。
// comma なら前に , を付ける。
static bool append_json_string(char **buf, size_t *len, size_t *cap,
                               const char *str, bool comma) {
  size_t n = strlen(str);
  // json_escape の最悪 (\u00XX) + , と引用符 + json_escape の NUL
  if (!buf_reserve(buf, cap, *len + n * 6 + 4))
    return false;
  if (comma)
    (*buf)[(*len)++] = ',';
  (*buf)[(*len)++] = '"';
  *len += json_escape(str, n, *buf + *len, *cap - *len);
  (*buf)[(*len)++] = '"';
  return true;
}

// `files` イベント: 変わったファイルの名前 (ブラウザでの path) の配列。
// 中身はブラウザが /files/ で取りにくる。
static char *build_sse_message(ServeState *s, const int *indices, int count,
                               bool include_lua) {
  size_t cap = 4096, len = 0;
  char *buf = (char *)malloc(cap);
  if (!buf)
    return NULL;
  len += (size_t)SDL_snprintf(buf, cap, "event: files\ndata: {\"files\":[");
  bool first = true;
  if (include_lua) {
    char disk[1024], web[768];
    entry_lua_paths(s, disk, sizeof(disk), web, sizeof(web));
    if (!append_json_string(&buf, &len, &cap, web, false)) {
      free(buf);
      return NULL;
    }
    first = false;
  }
  for (int i = 0; i < count; i++) {
    int idx = indices[i];
    if (idx < 0 || idx >= s->data_watch.count)
      continue;
    if (!append_json_string(&buf, &len, &cap,
                            s->data_watch.entries[idx].web_path, !first)) {
      free(buf);
      return NULL;
    }
    first = false;
  }
  if (!buf_reserve(&buf, &cap, len + 5)) {
    free(buf);
    return NULL;
  }
  memcpy(buf + len, "]}\n\n", 5);
  return buf;
}

// Build initial SSE with ALL game files
static char *build_initial_sse(ServeState *s) {
  int *all = (int *)malloc((size_t)(s->data_watch.count + 1) * sizeof(int));
  if (!all)
    return NULL;
  for (int i = 0; i < s->data_watch.count; i++)
    all[i] = i;
  char *msg = build_sse_message(s, all, s->data_watch.count, true);
  free(all);
  return msg;
}

static void send_sse_to_all(ServeState *s, const char *msg) {
  size_t len = strlen(msg);
  for (int i = 0; i < s->conn_count; i++) {
    if (!s->conns[i].is_sse)
      continue;
    if (!send_all(s->conns[i].fd, msg, len)) {
      // Client disconnected, close it
      sock_close(s->conns[i].fd);
      s->conns[i] = s->conns[--s->conn_count];
      i--;
    }
  }
}

// ---- connection management --------------------------------------------------

static void conn_close(ServeState *s, int idx) {
  sock_close(s->conns[idx].fd);
  s->conns[idx] = s->conns[--s->conn_count];
}

// ---- HTTP request handling --------------------------------------------------

static void handle_index(ServeState *s, int fd) {
  // {{ENTRY}} は生成 Lua のブラウザでの path (JS の文字列リテラルの中身)
  char disk[1024], web[768], entry[768 * 6 + 1];
  entry_lua_paths(s, disk, sizeof(disk), web, sizeof(web));
  json_escape(web, strlen(web), entry, sizeof(entry));
  size_t page_len = strlen(SERVE_PAGE_HTML);
  size_t entry_len = strlen(entry);
  // Rough upper bound for expanded page
  size_t cap = page_len + entry_len * 4 + 64;
  char *page = (char *)malloc(cap);
  if (!page) {
    send_http_response(fd, 500, "Internal Server Error", "text/plain", "OOM",
                       3);
    return;
  }
  // Simple template replace: {{ENTRY}}
  size_t pos = 0;
  const char *src = SERVE_PAGE_HTML;
  while (*src) {
    const char *marker = strstr(src, "{{ENTRY}}");
    if (!marker) {
      size_t remaining = strlen(src);
      memcpy(page + pos, src, remaining);
      pos += remaining;
      break;
    }
    size_t before = (size_t)(marker - src);
    memcpy(page + pos, src, before);
    pos += before;
    memcpy(page + pos, entry, entry_len);
    pos += entry_len;
    src = marker + 9; // strlen("{{ENTRY}}")
  }
  page[pos] = '\0';
  send_http_response(fd, 200, "OK", "text/html", page, pos);
  free(page);
}

static int hex_value(char c) {
  if (c >= '0' && c <= '9')
    return c - '0';
  if (c >= 'a' && c <= 'f')
    return c - 'a' + 10;
  if (c >= 'A' && c <= 'F')
    return c - 'A' + 10;
  return -1;
}

static bool url_decode(const char *in, char *out, size_t cap) {
  size_t w = 0;
  for (; *in; in++) {
    char c = *in;
    if (c == '%') {
      int hi = hex_value(in[1]), lo = hi < 0 ? -1 : hex_value(in[2]);
      if (lo < 0 || (hi == 0 && lo == 0))
        return false;
      c = (char)(hi * 16 + lo);
      in += 2;
    }
    if (w + 1 >= cap)
      return false;
    out[w++] = c;
  }
  out[w] = '\0';
  return true;
}

// GET /files/<path>: ゲームのファイルを中身のまま配る。
// 配るのは SSE で知らせた名前 (data watch と生成 Lua) だけで、それ以外の path は 404。
static void handle_file(ServeState *s, int fd, const char *url_path) {
  char web_path[512];
  const char *disk_path = NULL;
  char lua_disk[1024], lua_web[768];
  if (url_decode(url_path, web_path, sizeof(web_path))) {
    entry_lua_paths(s, lua_disk, sizeof(lua_disk), lua_web, sizeof(lua_web));
    if (strcmp(web_path, lua_web) == 0)
      disk_path = lua_disk;
    for (int i = 0; !disk_path && i < s->data_watch.count; i++)
      if (strcmp(web_path, s->data_watch.entries[i].web_path) == 0)
        disk_path = s->data_watch.entries[i].path;
  }
  if (!disk_path || !send_file_response(fd, disk_path))
    send_http_response(fd, 404, "Not Found", "text/plain", "Not Found", 9);
}

static void handle_request(ServeState *s, int conn_idx) {
  ServeConn *c = &s->conns[conn_idx];
  // Find the request line
  char *line_end = strstr(c->buf, "\r\n");
  if (!line_end)
    return;
  *line_end = '\0';

  // Parse "GET /path HTTP/1.1"
  char method[8] = {0}, path[512] = {0};
  if (sscanf(c->buf, "%7s %511s", method, path) != 2) {
    conn_close(s, conn_idx);
    return;
  }
  if (strcmp(method, "GET") != 0) {
    send_http_response(c->fd, 405, "Method Not Allowed", "text/plain",
                       "Only GET", 8);
    conn_close(s, conn_idx);
    return;
  }

  if (strcmp(path, "/") == 0) {
    handle_index(s, c->fd);
    conn_close(s, conn_idx);
  } else if (strcmp(path, "/host.js") == 0) {
    // Host bridge script from the game dir. The serve page always includes
    // <script src="/host.js">, so answer an empty script when the game has
    // none (see src/host.c for the window.lubHost contract).
    char file_path[1024];
    SDL_snprintf(file_path, sizeof(file_path), "%s/host.js", s->game_dir);
    if (!send_file_response(c->fd, file_path)) {
      send_http_response(c->fd, 200, "OK", "application/javascript", "", 0);
    }
    conn_close(s, conn_idx);
  } else if (strcmp(path, "/events") == 0) {
    // SSE endpoint
    if (!send_sse_headers(c->fd)) {
      conn_close(s, conn_idx);
      return;
    }
    c->is_sse = true;
    c->buf_len = 0;
    // Send initial file set
    char *msg = build_initial_sse(s);
    if (msg) {
      send_all(c->fd, msg, strlen(msg));
      free(msg);
    }
    SDL_Log("[serve] SSE client connected (fd=%d)", c->fd);
  } else if (strncmp(path, "/files/", 7) == 0) {
    handle_file(s, c->fd, path + 7);
    conn_close(s, conn_idx);
  } else if (strncmp(path, "/wasm/", 6) == 0) {
    char file_path[1024];
    SDL_snprintf(file_path, sizeof(file_path), "%s/%s", s->wasm_dir, path + 6);
    if (!send_file_response(c->fd, file_path)) {
      send_http_response(c->fd, 404, "Not Found", "text/plain", "Not Found", 9);
    }
    conn_close(s, conn_idx);
  } else if (strncmp(path, "/slang/", 7) == 0) {
    char file_path[1024];
    SDL_snprintf(file_path, sizeof(file_path), "%s/%s", s->slang_dir, path + 7);
    if (!send_file_response(c->fd, file_path)) {
      send_http_response(c->fd, 404, "Not Found", "text/plain", "Not Found", 9);
    }
    conn_close(s, conn_idx);
  } else {
    send_http_response(c->fd, 404, "Not Found", "text/plain", "Not Found", 9);
    conn_close(s, conn_idx);
  }
}

// ---- serve lifecycle --------------------------------------------------------

// ブラウザの MEMFS で game_dir を置く場所。
// ゲームは native と同じ cwd 基準の path で読むので、cwd (MEMFS の /) から game_dir への相対 path にする。
// cwd の外のゲームは game_dir を / に置く。
static void web_dir_of(const char *game_dir, char *out, size_t cap) {
  char dir[768];
  SDL_strlcpy(dir, game_dir, sizeof(dir));
  for (char *p = dir; *p; p++)
    if (*p == '\\')
      *p = '/';
  const char *rel = dir;
  char *cwd = SDL_GetCurrentDirectory(); // 区切りで終わる
  if (cwd) {
    for (char *p = cwd; *p; p++)
      if (*p == '\\')
        *p = '/';
    size_t n = strlen(cwd);
    if (strncmp(dir, cwd, n) == 0)
      rel = dir + n;
    else if (n > 0 && strncmp(dir, cwd, n - 1) == 0 && dir[n - 1] == '\0')
      rel = "";
  }
  while (rel[0] == '.' && rel[1] == '/')
    rel += 2;
  if (strcmp(rel, ".") == 0)
    rel = "";
  bool outside = rel[0] == '/' || (rel[0] && rel[1] == ':') ||
                 strcmp(rel, "..") == 0 || strncmp(rel, "../", 3) == 0 ||
                 strstr(rel, "/../") != NULL;
  if (outside)
    rel = "";
  size_t n = strlen(rel);
  SDL_snprintf(out, cap, "%s%s", rel, n > 0 && rel[n - 1] != '/' ? "/" : "");
  SDL_free(cwd);
}

bool serve_start(ServeState *s, const char *entry_path, const char *wasm_dir,
                 const char *slang_dir, int port) {
  if (!s || !entry_path)
    return false;
  SDL_zerop(s);
  s->port = port;

  SDL_strlcpy(s->entry_path, entry_path, sizeof(s->entry_path));
  path_dirname(entry_path, s->game_dir, sizeof(s->game_dir));
  path_basename_noext(entry_path, s->entry_name, sizeof(s->entry_name));
  web_dir_of(s->game_dir, s->web_dir, sizeof(s->web_dir));

  SDL_strlcpy(s->wasm_dir, wasm_dir, sizeof(s->wasm_dir));
  SDL_strlcpy(s->slang_dir, slang_dir, sizeof(s->slang_dir));

  // tcs pipeline (initial transpile + .cs watch)。
  // 生成 Lua は .lub/<Entry>.lua で、SSE の接続時の files イベントで知らせる。
  char out_lua[768];
  if (!tcs_pipeline_start(&s->tcs, entry_path, out_lua, sizeof(out_lua))) {
    SDL_Log("[serve] tcs pipeline start failed");
    return false;
  }

  // Init data file watcher (all non-source files in game dir; dot dirs such
  // as .lub/ are skipped)
  data_watch_init(&s->data_watch, s->game_dir, s->web_dir);

  // Start listening
  if (!sock_startup()) {
    SDL_Log("[serve] socket startup failed");
    tcs_pipeline_stop(&s->tcs);
    data_watch_shutdown(&s->data_watch);
    return false;
  }
  s->listen_fd = create_listen_socket(port);
  if (s->listen_fd < 0) {
    SDL_Log("[serve] failed to listen on port %d: %s", port, strerror(errno));
    tcs_pipeline_stop(&s->tcs);
    data_watch_shutdown(&s->data_watch);
    return false;
  }

  SDL_Log("[serve] listening on http://localhost:%d", port);
  SDL_Log("[serve] entry: %s  game_dir: %s  in the browser: /%s", s->entry_name,
          s->game_dir, s->web_dir);
  return true;
}

bool serve_tick(ServeState *s) {
  if (!s || s->listen_fd < 0)
    return false;

  // Build poll set: listen_fd + all connections
  struct pollfd pfds[SERVE_MAX_CONNS + 1];
  int npfds = 0;
  pfds[npfds].fd = s->listen_fd;
  pfds[npfds].events = POLLIN;
  npfds++;
  for (int i = 0; i < s->conn_count; i++) {
    pfds[npfds].fd = s->conns[i].fd;
    pfds[npfds].events = s->conns[i].is_sse ? 0 : POLLIN;
    npfds++;
  }

  // Poll with short timeout (10ms) to keep tick responsive
  int ready = poll(pfds, (nfds_t)npfds, 10);
  if (ready < 0 && errno != EINTR)
    return false;

  // Accept new connections
  if (pfds[0].revents & POLLIN) {
    int client_fd = (int)accept(s->listen_fd, NULL, NULL);
    if (client_fd >= 0) {
      if (s->conn_count < SERVE_MAX_CONNS) {
        sock_set_nonblocking(client_fd);
        ServeConn *c = &s->conns[s->conn_count++];
        c->fd = client_fd;
        c->is_sse = false;
        c->buf_len = 0;
        memset(c->buf, 0, sizeof(c->buf));
      } else {
        sock_close(client_fd);
      }
    }
  }

  // Read from non-SSE connections
  for (int i = 0; i < s->conn_count; i++) {
    if (s->conns[i].is_sse)
      continue;
    int pfd_idx = i + 1;
    if (pfd_idx >= npfds)
      break;
    if (!(pfds[pfd_idx].revents & POLLIN))
      continue;

    ServeConn *c = &s->conns[i];
    ssize_t n = recv(c->fd, c->buf + c->buf_len,
                     (int)(sizeof(c->buf) - (size_t)c->buf_len - 1), 0);
    if (n <= 0) {
      conn_close(s, i);
      i--;
      continue;
    }
    c->buf_len += (int)n;
    c->buf[c->buf_len] = '\0';

    // Check for complete HTTP request (ends with \r\n\r\n)
    if (strstr(c->buf, "\r\n\r\n")) {
      handle_request(s, i);
      // handle_request may have removed this connection; adjust index
      // (conn_close moves last conn to this slot, so re-check i)
    }
  }

  // Check for SSE client disconnects
  for (int i = 0; i < s->conn_count; i++) {
    if (!s->conns[i].is_sse)
      continue;
    // Try a zero-length recv to detect disconnect
    char tmp;
    ssize_t n = recv(s->conns[i].fd, &tmp, 1, MSG_PEEK | MSG_DONTWAIT);
    if (n == 0) {
      SDL_Log("[serve] SSE client disconnected (fd=%d)", s->conns[i].fd);
      conn_close(s, i);
      i--;
    }
  }

  // tcs の frame は 1 つずつ SSE のイベントで送る (data は本文の JSON 文字列)。
  // reload chunk は reload イベントで、当てるのはブラウザ側の runtime (lub_queue_reload_chunk_file)。
  // restart_required は restart イベントで、ページが理由を出して読み直し、書き終わっている出力から起動し直す。
  TcsFrameKind kind;
  char *body;
  size_t body_len;
  while (tcs_pipeline_next_frame(&s->tcs, &kind, &body, &body_len)) {
    const char *event =
        kind == TCS_FRAME_RESTART_REQUIRED ? "restart" : "reload";
    if (kind == TCS_FRAME_RESTART_REQUIRED)
      SDL_Log("[serve] tcs: restart required, reloading the page:\n%s", body);
    size_t cap = body_len * 6 + 64; // json_escape の最悪 (\u00XX) + 枠
    char *msg = (char *)malloc(cap);
    if (msg) {
      size_t pos = (size_t)SDL_snprintf(msg, cap, "event: %s\ndata: \"", event);
      pos += json_escape(body, body_len, msg + pos, cap - pos);
      SDL_snprintf(msg + pos, cap - pos, "\"\n\n");
      send_sse_to_all(s, msg);
      free(msg);
    }
    SDL_free(body);
  }

  // Tick data watch (.lub/<Entry>.lua は data watch の外で、更新は上の tcs の frame で届く)
  int *changed =
      (int *)SDL_malloc((size_t)(s->data_watch.count + 1) * sizeof(int));
  if (changed) {
    int nchanged = data_watch_tick(&s->data_watch, changed);
    if (nchanged > 0) {
      SDL_Log("[serve] %d data file(s) changed, sending update", nchanged);
      char *msg = build_sse_message(s, changed, nchanged, false);
      if (msg) {
        send_sse_to_all(s, msg);
        free(msg);
      }
    }
    SDL_free(changed);
  }

  return true;
}

void serve_stop(ServeState *s) {
  if (!s)
    return;
  for (int i = 0; i < s->conn_count; i++)
    sock_close(s->conns[i].fd);
  s->conn_count = 0;
  if (s->listen_fd >= 0) {
    sock_close(s->listen_fd);
    s->listen_fd = -1;
  }
  tcs_pipeline_stop(&s->tcs);
  data_watch_shutdown(&s->data_watch);
  SDL_Log("[serve] stopped");
}
