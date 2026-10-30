#include "../../src/serve.h"
#include "../../src/sock_compat.h"
#include <SDL3/SDL.h>
#include <errno.h>
#include <stdio.h>
#include <string.h>

// Smoke test for lub --serve. Single-threaded: interleaves serve_tick with
// non-blocking client I/O.

static int nb_connect(int port) {
  int fd = (int)socket(AF_INET, SOCK_STREAM, 0);
  if (fd < 0)
    return -1;
  struct sockaddr_in addr = {0};
  addr.sin_family = AF_INET;
  addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  addr.sin_port = htons((uint16_t)port);
  if (connect(fd, (struct sockaddr *)&addr, sizeof(addr)) < 0) {
    sock_close(fd);
    return -1;
  }
  sock_set_nonblocking(fd);
  return fd;
}

// Send request, tick server, read response. Returns bytes read.
static int do_request(ServeState *s, int port, const char *path, char *out,
                      size_t out_sz) {
  int fd = nb_connect(port);
  if (fd < 0)
    return -1;

  char req[256];
  int rlen = SDL_snprintf(req, sizeof(req),
                          "GET %s HTTP/1.1\r\nHost: localhost\r\n\r\n", path);
  send(fd, req, rlen, 0);

  // Tick server to process the connection
  for (int i = 0; i < 20; i++) {
    serve_tick(s);
    SDL_Delay(10);
  }

  size_t total = 0;
  for (int attempt = 0; attempt < 10 && total < out_sz - 1; attempt++) {
    ssize_t n = recv(fd, out + total, (int)(out_sz - 1 - total), 0);
    if (n > 0)
      total += (size_t)n;
    else if (n == 0)
      break;
    else if (sock_would_block()) {
      serve_tick(s);
      SDL_Delay(10);
    } else
      break;
  }
  sock_close(fd);
  out[total] = '\0';
  return (int)total;
}

static bool test_http_index(ServeState *s, int port) {
  char buf[8192] = {0};
  int n = do_request(s, port, "/", buf, sizeof(buf));
  if (n <= 0) {
    SDL_Log("FAIL: index: no response");
    return false;
  }
  if (!strstr(buf, "HTTP/1.1 200")) {
    SDL_Log("FAIL: index: not 200");
    return false;
  }
  if (!strstr(buf, "text/html")) {
    SDL_Log("FAIL: index: wrong content-type");
    return false;
  }
  if (!strstr(buf, "EventSource")) {
    SDL_Log("FAIL: index: missing EventSource JS");
    return false;
  }
  SDL_Log("PASS: HTTP index");
  return true;
}

static bool test_http_404(ServeState *s, int port) {
  char buf[4096] = {0};
  int n = do_request(s, port, "/nonexistent", buf, sizeof(buf));
  if (n <= 0) {
    SDL_Log("FAIL: 404: no response");
    return false;
  }
  if (!strstr(buf, "404")) {
    SDL_Log("FAIL: 404: expected 404");
    return false;
  }
  SDL_Log("PASS: HTTP 404");
  return true;
}

static bool test_sse_connect(ServeState *s, int port) {
  int fd = nb_connect(port);
  if (fd < 0) {
    SDL_Log("FAIL: SSE: connect failed");
    return false;
  }

  const char *req = "GET /events HTTP/1.1\r\nHost: localhost\r\n\r\n";
  send(fd, req, (int)strlen(req), 0);

  // Tick server many times to accept, handle request, and send initial data
  for (int i = 0; i < 30; i++) {
    serve_tick(s);
    SDL_Delay(10);
  }

  char buf[65536] = {0};
  size_t total = 0;
  for (int attempt = 0; attempt < 20 && total < sizeof(buf) - 1; attempt++) {
    ssize_t n = recv(fd, buf + total, (int)(sizeof(buf) - 1 - total), 0);
    if (n > 0)
      total += (size_t)n;
    else if (n == 0)
      break;
    else if (sock_would_block()) {
      serve_tick(s);
      SDL_Delay(20);
    } else
      break;
  }
  sock_close(fd);
  buf[total] = '\0';

  if (!strstr(buf, "text/event-stream")) {
    SDL_Log("FAIL: SSE: missing event-stream type. Got: %.200s", buf);
    return false;
  }
  if (!strstr(buf, "event: files")) {
    SDL_Log("FAIL: SSE: missing initial event. Got: %.500s", buf);
    return false;
  }
  // 名前だけを cwd 基準の path で送る (中身は /files/ で取る)
  if (!strstr(buf, "\"files\":[") ||
      !strstr(buf, "\"samples/03_texture/data/03_tex.png\"") ||
      !strstr(buf, "\"samples/03_texture/.lub/Texture03.lua\"")) {
    SDL_Log("FAIL: SSE: missing file names. Got: %.500s", buf);
    return false;
  }
  SDL_Log("PASS: SSE connect + initial files");
  return true;
}

// binary を壊さずに配る
static bool test_files_binary(ServeState *s, int port) {
  const char *disk = "samples/03_texture/data/03_tex.png";
  size_t want_len = 0;
  void *want = SDL_LoadFile(disk, &want_len);
  if (!want) {
    SDL_Log("FAIL: files: cannot read %s", disk);
    return false;
  }
  static char buf[65536];
  int n = do_request(s, port, "/files/samples/03_texture/data/03_tex.png", buf,
                     sizeof(buf));
  const char *body = n > 0 ? strstr(buf, "\r\n\r\n") : NULL;
  bool ok = body && strstr(buf, "HTTP/1.1 200") &&
            (size_t)(n - (body + 4 - buf)) == want_len &&
            memcmp(body + 4, want, want_len) == 0;
  SDL_free(want);
  if (!ok) {
    SDL_Log("FAIL: files: %s differs from disk (%d bytes received)", disk, n);
    return false;
  }
  SDL_Log("PASS: files binary");
  return true;
}

// 配るのは見ているゲームのファイルだけ
static bool test_files_outside(ServeState *s, int port) {
  const char *paths[] = {"/files/samples/03_texture/Texture03.cs",
                         "/files/samples/03_texture/../../CMakeLists.txt",
                         "/files/CMakeLists.txt"};
  for (size_t i = 0; i < SDL_arraysize(paths); i++) {
    char buf[4096] = {0};
    do_request(s, port, paths[i], buf, sizeof(buf));
    if (!strstr(buf, "HTTP/1.1 404")) {
      SDL_Log("FAIL: files: %s was not 404", paths[i]);
      return false;
    }
  }
  SDL_Log("PASS: files outside the game");
  return true;
}

// 相手が閉じた接続を片づける tick で、まだ request を送っていない隣の接続を巻き込まない。
// conn_close は末尾の接続を閉じた slot に詰めるので、slot の位置で revents を引くと、閉じた接続の POLLIN で隣を recv して EAGAIN で閉じてしまう。
static bool test_close_keeps_neighbor(ServeState *s, int port) {
  int a = nb_connect(port);
  int b = nb_connect(port);
  if (a < 0 || b < 0) {
    SDL_Log("FAIL: neighbor: connect failed");
    return false;
  }
  // accept は tick に 1 本なので、両方が入るまで回す
  for (int i = 0; i < 5; i++) {
    serve_tick(s);
    SDL_Delay(10);
  }
  sock_close(a);
  // a の EOF を片づける tick
  for (int i = 0; i < 5; i++) {
    serve_tick(s);
    SDL_Delay(10);
  }
  const char *req = "GET / HTTP/1.1\r\nHost: localhost\r\n\r\n";
  send(b, req, (int)strlen(req), 0);
  char buf[8192] = {0};
  size_t total = 0;
  for (int attempt = 0; attempt < 20 && total < sizeof(buf) - 1; attempt++) {
    serve_tick(s);
    SDL_Delay(10);
    ssize_t n = recv(b, buf + total, (int)(sizeof(buf) - 1 - total), 0);
    if (n > 0)
      total += (size_t)n;
    else if (n == 0 || !sock_would_block())
      break;
  }
  sock_close(b);
  buf[total] = '\0';
  if (!strstr(buf, "HTTP/1.1 200")) {
    SDL_Log("FAIL: neighbor: the connection next to a closed one got no "
            "response (%zu bytes)",
            total);
    return false;
  }
  SDL_Log("PASS: closing a connection keeps its neighbor");
  return true;
}

// Windows の SDL は enumerate の dir を \\ 区切りで渡す
static bool test_web_path(void) {
  const char *cases[][3] = {
      {"samples/g/", "\\data\\x.png", "samples/g/data/x.png"},
      {"samples/g/", "\\.lub\\Game.lua", "samples/g/.lub/Game.lua"},
      {"", "/data/x.png", "data/x.png"},
  };
  for (size_t i = 0; i < SDL_arraysize(cases); i++) {
    char out[128];
    serve_web_path(cases[i][0], cases[i][1], out, sizeof(out));
    if (strcmp(out, cases[i][2]) != 0) {
      SDL_Log("FAIL: web path: %s%s -> %s", cases[i][0], cases[i][1], out);
      return false;
    }
  }
  SDL_Log("PASS: web path");
  return true;
}

// SSE の 1 イベントを tick しながら待つ。末尾の空行まで読めたら true。
static bool sse_next_event(ServeState *s, int fd, char *buf, size_t cap) {
  size_t total = 0;
  buf[0] = '\0';
  for (int attempt = 0; attempt < 200; attempt++) {
    serve_tick(s);
    SDL_Delay(5);
    ssize_t n = recv(fd, buf + total, (int)(cap - 1 - total), 0);
    if (n > 0) {
      total += (size_t)n;
      buf[total] = '\0';
      if (strstr(buf, "event: files") && strstr(buf, "\n\n"))
        return true;
    }
  }
  return false;
}

// 起動後に足したファイルと .lub/ の下を知らせ、/files/ で配る。
// 生成 Lua は reload chunk で届くので更新の files に入れず、書きかけの tmp も入れない。
static bool test_watch_added(ServeState *s, int port) {
  const char *data = "samples/03_texture/data/serve_smoke_added.txt";
  const char *lub = "samples/03_texture/.lub/serve_smoke_added.txt";
  const char *tmp = "samples/03_texture/.lub/.serve_smoke_added.tmp";
  const char *const added[] = {data, lub};
  bool ok = false;
  static char buf[65536];

  int fd = nb_connect(port);
  if (fd < 0) {
    SDL_Log("FAIL: watch: connect failed");
    return false;
  }
  const char *req = "GET /events HTTP/1.1\r\nHost: localhost\r\n\r\n";
  send(fd, req, (int)strlen(req), 0);
  if (!sse_next_event(s, fd, buf, sizeof(buf))) {
    SDL_Log("FAIL: watch: no initial files event");
    goto done;
  }

  for (size_t i = 0; i < SDL_arraysize(added); i++) {
    SDL_SaveFile(tmp, "x", 1);
    SDL_SaveFile(added[i], "added", 5);
    char name[128];
    SDL_snprintf(name, sizeof(name), "\"%s\"", added[i]);
    if (!sse_next_event(s, fd, buf, sizeof(buf)) || !strstr(buf, name)) {
      SDL_Log("FAIL: watch: added %s was not announced. Got: %.300s", added[i],
              buf);
      goto done;
    }
    if (strstr(buf, "Texture03.lua") || strstr(buf, ".serve_smoke_added.tmp")) {
      SDL_Log("FAIL: watch: update has the entry or a tmp file. Got: %.300s",
              buf);
      goto done;
    }
    char url[160], body[4096];
    SDL_snprintf(url, sizeof(url), "/files/%s", added[i]);
    do_request(s, port, url, body, sizeof(body));
    if (!strstr(body, "HTTP/1.1 200") || !strstr(body, "\r\n\r\nadded")) {
      SDL_Log("FAIL: watch: %s is not served. Got: %.200s", url, body);
      goto done;
    }

    // 足した後の変更も届く (mtime の粒度より待つ)
    SDL_Delay(20);
    SDL_SaveFile(added[i], "edited", 6);
    if (!sse_next_event(s, fd, buf, sizeof(buf)) || !strstr(buf, name)) {
      SDL_Log("FAIL: watch: edit of %s was not announced. Got: %.300s",
              added[i], buf);
      goto done;
    }
  }
  ok = true;
  SDL_Log("PASS: files added after start and under .lub/ are watched");

done:
  sock_close(fd);
  SDL_RemovePath(data);
  SDL_RemovePath(lub);
  SDL_RemovePath(tmp);
  return ok;
}

int main(int argc, char *argv[]) {
  (void)argc;
  (void)argv;

  const char *entry = "samples/03_texture/Texture03.csproj";
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(entry, &info)) {
    SDL_Log("SKIP: %s not found (run from lub root)", entry);
    return 0;
  }

  // /wasm/ と /slang/ は試さないので、無くてもよい
  const char *wasm_dir = "build/wasm";
  const char *slang_dir = "web/public/slang";

  ServeState s;
  int port = 18080;
  if (!serve_start(&s, entry, wasm_dir, slang_dir, port)) {
    SDL_Log("FAIL: serve_start failed");
    return 1;
  }

  int failures = 0;
  if (!test_http_index(&s, port))
    failures++;
  if (!test_http_404(&s, port))
    failures++;
  if (!test_sse_connect(&s, port))
    failures++;
  if (!test_files_binary(&s, port))
    failures++;
  if (!test_files_outside(&s, port))
    failures++;
  if (!test_close_keeps_neighbor(&s, port))
    failures++;
  if (!test_watch_added(&s, port))
    failures++;
  if (!test_web_path())
    failures++;

  serve_stop(&s);

  if (failures > 0) {
    SDL_Log("%d test(s) failed", failures);
    return 1;
  }
  SDL_Log("All serve smoke tests passed");
  return 0;
}
