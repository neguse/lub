#include "serve.h"
#include "embedded_serve_page.h"
#include "haxe_build.h"
#include <SDL3/SDL.h>
#include <errno.h>
#include <fcntl.h>
#include <netinet/in.h>
#include <poll.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/socket.h>
#include <unistd.h>

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

static int make_nonblocking(int fd) {
  int flags = fcntl(fd, F_GETFL, 0);
  if (flags < 0)
    return -1;
  return fcntl(fd, F_SETFL, flags | O_NONBLOCK);
}

static int create_listen_socket(int port) {
  int fd = socket(AF_INET, SOCK_STREAM, 0);
  if (fd < 0)
    return -1;
  int opt = 1;
  setsockopt(fd, SOL_SOCKET, SO_REUSEADDR, &opt, sizeof(opt));
  struct sockaddr_in addr = {0};
  addr.sin_family = AF_INET;
  addr.sin_addr.s_addr = htonl(INADDR_ANY);
  addr.sin_port = htons((uint16_t)port);
  if (bind(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
    close(fd);
    return -1;
  }
  if (listen(fd, 8) < 0) {
    close(fd);
    return -1;
  }
  make_nonblocking(fd);
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
    ssize_t n = send(fd, data + sent, len - sent, MSG_NOSIGNAL);
    if (n < 0) {
      if (errno == EAGAIN || errno == EWOULDBLOCK)
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
  FILE *f = fopen(file_path, "rb");
  if (!f)
    return false;
  fseek(f, 0, SEEK_END);
  long sz = ftell(f);
  fseek(f, 0, SEEK_SET);
  if (sz < 0 || sz > 64 * 1024 * 1024) {
    fclose(f);
    return false;
  }
  char *buf = (char *)malloc((size_t)sz);
  if (!buf) {
    fclose(f);
    return false;
  }
  fread(buf, 1, (size_t)sz, f);
  fclose(f);
  bool ok = send_http_response(fd, 200, "OK", content_type_for(file_path), buf,
                               (size_t)sz);
  free(buf);
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
                      const char *rel_path) {
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
  SDL_strlcpy(w->entries[w->count].rel_path, rel_path,
              sizeof(w->entries[0].rel_path));
  w->entries[w->count].mtime_ns = mt;
  w->count++;
}

typedef struct {
  ServeDataWatch *watch;
  const char *base_dir;
  size_t base_len;
} WatchEnumCtx;

static SDL_EnumerationResult SDLCALL watch_enum_cb(void *userdata,
                                                   const char *dir,
                                                   const char *fname) {
  WatchEnumCtx *ctx = (WatchEnumCtx *)userdata;
  if (!fname || fname[0] == '.')
    return SDL_ENUM_CONTINUE;

  char full[768];
  SDL_snprintf(full, sizeof(full), "%s/%s", dir, fname);

  SDL_PathInfo info;
  if (!SDL_GetPathInfo(full, &info))
    return SDL_ENUM_CONTINUE;

  if (info.type == SDL_PATHTYPE_DIRECTORY) {
    SDL_EnumerateDirectory(full, watch_enum_cb, userdata);
  } else if (info.type == SDL_PATHTYPE_FILE) {
    size_t n = strlen(fname);
    // skip .hx files (handled by haxe_pipeline)
    if (n > 3 && SDL_strcasecmp(fname + n - 3, ".hx") == 0)
      return SDL_ENUM_CONTINUE;
    // skip .hxml files
    if (n > 5 && SDL_strcasecmp(fname + n - 5, ".hxml") == 0)
      return SDL_ENUM_CONTINUE;

    const char *rel = full + ctx->base_len;
    if (*rel == '/')
      rel++;
    watch_add(ctx->watch, full, rel);
  }
  return SDL_ENUM_CONTINUE;
}

static bool data_watch_init(ServeDataWatch *w, const char *game_dir) {
  SDL_zerop(w);
  WatchEnumCtx ctx = {w, game_dir, strlen(game_dir)};
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

// Re-scan directory for new files (e.g., after haxe creates .lub/)
static void data_watch_rescan(ServeDataWatch *w, const char *game_dir) {
  // Save old entries to detect truly new files
  int old_count = w->count;
  ServeWatchEntry *old = w->entries;
  w->entries = NULL;
  w->count = 0;
  w->cap = 0;

  WatchEnumCtx ctx = {w, game_dir, strlen(game_dir)};
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

// Read a file and append "\"rel_path\":\"escaped_content\"" to buf.
// Returns bytes written (excluding null terminator), or 0 on failure.
static size_t append_file_json(char *buf, size_t cap, size_t pos,
                               const char *file_path, const char *rel_path) {
  FILE *f = fopen(file_path, "rb");
  if (!f)
    return 0;
  fseek(f, 0, SEEK_END);
  long sz = ftell(f);
  fseek(f, 0, SEEK_SET);
  if (sz < 0 || sz > 4 * 1024 * 1024) {
    fclose(f);
    return 0;
  }
  char *content = (char *)malloc((size_t)sz + 1);
  if (!content) {
    fclose(f);
    return 0;
  }
  fread(content, 1, (size_t)sz, f);
  content[sz] = '\0';
  fclose(f);

  // Estimate escaped size: worst case 6x for all control chars
  size_t esc_cap = (size_t)sz * 6 + 1;
  char *escaped = (char *)malloc(esc_cap);
  if (!escaped) {
    free(content);
    return 0;
  }
  size_t esc_len = json_escape(content, (size_t)sz, escaped, esc_cap);
  free(content);

  size_t needed = strlen(rel_path) + esc_len + 6; // "key":"val",
  if (pos + needed >= cap) {
    free(escaped);
    return 0;
  }

  int w = SDL_snprintf(buf + pos, cap - pos, "\"%s\":\"", rel_path);
  pos += (size_t)w;
  memcpy(buf + pos, escaped, esc_len);
  pos += esc_len;
  buf[pos++] = '"';
  free(escaped);
  return pos;
}

// Build SSE message with given files. Returns malloc'd string or NULL.
// indices/count: which data_watch entries to include
// include_lua: if true, also include the compiled .lua
static char *build_sse_message(ServeState *s, const int *indices, int count,
                               bool include_lua) {
  size_t cap = 1024 * 1024; // 1MB initial
  char *buf = (char *)malloc(cap);
  if (!buf)
    return NULL;

  size_t pos = 0;
  // SSE format: event: files\ndata: {json}\n\n
  pos += (size_t)SDL_snprintf(buf + pos, cap - pos,
                              "event: files\ndata: {\"files\":{");

  bool first = true;

  if (include_lua) {
    char lua_path[768];
    SDL_snprintf(lua_path, sizeof(lua_path), "%s/.lub/%s.lua", s->game_dir,
                 s->entry_name);
    char lua_rel[512];
    SDL_snprintf(lua_rel, sizeof(lua_rel), ".lub/%s.lua", s->entry_name);
    if (!first) {
      buf[pos++] = ',';
    }
    size_t new_pos = append_file_json(buf, cap, pos, lua_path, lua_rel);
    if (new_pos > 0) {
      pos = new_pos;
      first = false;
    }
  }

  for (int i = 0; i < count; i++) {
    int idx = indices[i];
    if (idx < 0 || idx >= s->data_watch.count)
      continue;
    if (!first) {
      buf[pos++] = ',';
    }
    size_t new_pos =
        append_file_json(buf, cap, pos, s->data_watch.entries[idx].path,
                         s->data_watch.entries[idx].rel_path);
    if (new_pos > 0) {
      pos = new_pos;
      first = false;
    }
    // Grow buffer if getting close
    if (pos > cap - 1024 * 256) {
      cap *= 2;
      char *grown = (char *)realloc(buf, cap);
      if (!grown) {
        free(buf);
        return NULL;
      }
      buf = grown;
    }
  }

  pos += (size_t)SDL_snprintf(buf + pos, cap - pos, "}}\n\n");
  buf[pos] = '\0';
  return buf;
}

// Build initial SSE with ALL game files
static char *build_initial_sse(ServeState *s) {
  int *all = (int *)malloc((size_t)s->data_watch.count * sizeof(int));
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
      close(s->conns[i].fd);
      s->conns[i] = s->conns[--s->conn_count];
      i--;
    }
  }
}

// ---- connection management --------------------------------------------------

static void conn_close(ServeState *s, int idx) {
  close(s->conns[idx].fd);
  s->conns[idx] = s->conns[--s->conn_count];
}

// ---- HTTP request handling --------------------------------------------------

static void handle_index(ServeState *s, int fd) {
  // Replace {{ENTRY}} placeholder in the embedded HTML
  size_t page_len = strlen(SERVE_PAGE_HTML);
  size_t entry_len = strlen(s->entry_name);
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
    memcpy(page + pos, s->entry_name, entry_len);
    pos += entry_len;
    src = marker + 9; // strlen("{{ENTRY}}")
  }
  page[pos] = '\0';
  send_http_response(fd, 200, "OK", "text/html", page, pos);
  free(page);
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

bool serve_start(ServeState *s, const char *hxml_path, const char *wasm_dir,
                 const char *slang_dir, int port) {
  if (!s || !hxml_path)
    return false;
  SDL_zerop(s);
  s->port = port;

  SDL_strlcpy(s->hxml_path, hxml_path, sizeof(s->hxml_path));
  path_dirname(hxml_path, s->game_dir, sizeof(s->game_dir));
  path_basename_noext(hxml_path, s->entry_name, sizeof(s->entry_name));

  SDL_strlcpy(s->wasm_dir, wasm_dir, sizeof(s->wasm_dir));
  SDL_strlcpy(s->slang_dir, slang_dir, sizeof(s->slang_dir));

  // Start haxe pipeline (server + initial build + .hx watch)
  if (!haxe_pipeline_start(&s->haxe, hxml_path)) {
    SDL_Log("[serve] haxe pipeline start failed");
    return false;
  }

  // Init data file watcher (all non-.hx files in game dir)
  data_watch_init(&s->data_watch, s->game_dir);
  // Rescan to pick up .lub/ created by initial build
  data_watch_rescan(&s->data_watch, s->game_dir);

  // Start listening
  s->listen_fd = create_listen_socket(port);
  if (s->listen_fd < 0) {
    SDL_Log("[serve] failed to listen on port %d: %s", port, strerror(errno));
    haxe_pipeline_stop(&s->haxe);
    data_watch_shutdown(&s->data_watch);
    return false;
  }

  SDL_Log("[serve] listening on http://localhost:%d", port);
  SDL_Log("[serve] entry: %s  game_dir: %s", s->entry_name, s->game_dir);
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
    int client_fd = accept(s->listen_fd, NULL, NULL);
    if (client_fd >= 0) {
      if (s->conn_count < SERVE_MAX_CONNS) {
        make_nonblocking(client_fd);
        ServeConn *c = &s->conns[s->conn_count++];
        c->fd = client_fd;
        c->is_sse = false;
        c->buf_len = 0;
        memset(c->buf, 0, sizeof(c->buf));
      } else {
        close(client_fd);
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
                     sizeof(c->buf) - (size_t)c->buf_len - 1, 0);
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

  // Tick haxe pipeline
  bool haxe_rebuilt = haxe_pipeline_tick(&s->haxe);
  if (haxe_rebuilt) {
    SDL_Log("[serve] haxe rebuild complete, sending .lua");
    // Rescan to pick up newly created files
    data_watch_rescan(&s->data_watch, s->game_dir);
    char *msg = build_sse_message(s, NULL, 0, true);
    if (msg) {
      send_sse_to_all(s, msg);
      free(msg);
    }
  }

  // Tick data watch
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
    close(s->conns[i].fd);
  s->conn_count = 0;
  if (s->listen_fd >= 0) {
    close(s->listen_fd);
    s->listen_fd = -1;
  }
  haxe_pipeline_stop(&s->haxe);
  data_watch_shutdown(&s->data_watch);
  SDL_Log("[serve] stopped");
}
