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
  if (!strstr(buf, "\"files\":{")) {
    SDL_Log("FAIL: SSE: missing files JSON");
    return false;
  }
  SDL_Log("PASS: SSE connect + initial files");
  return true;
}

int main(int argc, char *argv[]) {
  (void)argc;
  (void)argv;

  const char *hxml = "samples/00_hello/00_hello.hxml";
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(hxml, &info)) {
    SDL_Log("SKIP: %s not found (run from lub root)", hxml);
    return 0;
  }

  const char *wasm_dir = "build/wasm";
  const char *slang_dir = "web/public/slang";
  if (!SDL_GetPathInfo(wasm_dir, &info)) {
    SDL_Log("SKIP: %s not found (WASM build required)", wasm_dir);
    return 0;
  }

  ServeState s;
  int port = 18080;
  if (!serve_start(&s, hxml, wasm_dir, slang_dir, port)) {
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

  serve_stop(&s);

  if (failures > 0) {
    SDL_Log("%d test(s) failed", failures);
    return 1;
  }
  SDL_Log("All serve smoke tests passed");
  return 0;
}
