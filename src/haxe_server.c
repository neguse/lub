#include "haxe_server.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

// haxe --wait は標準では何も出力しないため、stdout のメッセージで listening
// 完了を 検知することはできない。spawn 後しばらく (READY_TIMEOUT_MS) は alive
// check を 繰り返し、その間 1 度も exit していなければ listening
// 中とみなす。port 衝突時 は haxe が "Couldn't wait on ..." を吐いて即 exit
// するので非常に高速に判別できる。
#define READY_TIMEOUT_MS 1500
#define READY_POLL_MS 50

const char *haxe_bin(void) {
  const char *h = SDL_getenv("LUB_HAXE");
  return (h && h[0]) ? h : "haxe";
}

// `LUB_HAXE` 指定時、HAXE_STD_PATH が未設定なら <dirname(LUB_HAXE)>/std
// を補完する。 公式 tarball の haxe バイナリは std を自動検出せず HAXE_STD_PATH
// を要求するため (system haxe は通常パッケージが std パスを解決済み)。--wait
// サーバが継承して使う。
static void ensure_haxe_std_path(void) {
  const char *lub_haxe = SDL_getenv("LUB_HAXE");
  if (!lub_haxe || !lub_haxe[0])
    return;
  if (SDL_getenv("HAXE_STD_PATH"))
    return;
  const char *slash = SDL_strrchr(lub_haxe, '/');
  if (!slash)
    return; // PATH 解決される名前のみ: std 位置を推定できないので何もしない
  size_t dirlen = (size_t)(slash - lub_haxe);
  char std_path[1024];
  if (dirlen + 5 >= sizeof(std_path))
    return;
  SDL_memcpy(std_path, lub_haxe, dirlen);
  SDL_snprintf(std_path + dirlen, sizeof(std_path) - dirlen, "/std");
  setenv("HAXE_STD_PATH", std_path, 0); // overwrite=0: 既存があれば尊重
}

static void drain_stdio(SDL_Process *p) {
  // pipe を放置すると buffer が埋まって子が block しうるので、捨て読みする。
  // stdout / stderr 両方ノンブロッキングなので available 分だけ読めば良い。
  SDL_IOStream *out = SDL_GetProcessOutput(p);
  if (out) {
    char buf[256];
    while (SDL_ReadIO(out, buf, sizeof(buf)) > 0) {
      /* discard */
    }
  }
}

static bool try_spawn_one(HaxeServer *s, int port) {
  char port_str[16];
  SDL_snprintf(port_str, sizeof(port_str), "%d", port);
  const char *argv[] = {haxe_bin(), "--wait", port_str, NULL};

  SDL_PropertiesID props = SDL_CreateProperties();
  if (props == 0) {
    SDL_Log("haxe --wait %d: SDL_CreateProperties failed: %s", port,
            SDL_GetError());
    return false;
  }
  SDL_SetPointerProperty(props, SDL_PROP_PROCESS_CREATE_ARGS_POINTER,
                         (void *)argv);
  // stdout/stderr を APP に向けて、（a）子が端末に直書きするのを止め、
  // （b）後で読み捨てられる pipe にする。stderr_to_stdout を true にして 1 本に
  // まとめると drain も 1 stream で済む。
  SDL_SetNumberProperty(props, SDL_PROP_PROCESS_CREATE_STDOUT_NUMBER,
                        SDL_PROCESS_STDIO_APP);
  SDL_SetBooleanProperty(
      props, SDL_PROP_PROCESS_CREATE_STDERR_TO_STDOUT_BOOLEAN, true);

  SDL_Process *p = SDL_CreateProcessWithProperties(props);
  SDL_DestroyProperties(props);
  if (!p) {
    SDL_Log("haxe --wait %d spawn failed: %s", port, SDL_GetError());
    return false;
  }

  // poll: 子プロセスが READY_TIMEOUT_MS 経過後もまだ alive なら listening
  // しているとみなす。途中で exit したら port 衝突等として失敗扱い。
  Uint64 start = SDL_GetTicks();
  while (SDL_GetTicks() - start < READY_TIMEOUT_MS) {
    SDL_Delay(READY_POLL_MS);
    drain_stdio(p);
    int exit_code = 0;
    if (SDL_WaitProcess(p, false, &exit_code)) {
      // 子が終了 — port 衝突 or 起動失敗。
      SDL_DestroyProcess(p);
      return false;
    }
  }

  s->child = p;
  s->port = port;
  s->ready = true;
  return true;
}

bool haxe_server_start(HaxeServer *s) {
  if (!s)
    return false;
  SDL_zero(*s);
  ensure_haxe_std_path();
  const char *env = SDL_getenv("LUB_HAXE_PORT");
  if (env && env[0]) {
    int p = atoi(env);
    if (p <= 0 || p > 65535) {
      SDL_Log("haxe_server_start: invalid LUB_HAXE_PORT='%s'", env);
      return false;
    }
    return try_spawn_one(s, p);
  }
  for (int p = 7400; p <= 7410; ++p) {
    if (try_spawn_one(s, p))
      return true;
  }
  SDL_Log("haxe --wait: no free port in 7400..7410");
  return false;
}

void haxe_server_stop(HaxeServer *s) {
  if (!s || !s->child)
    return;
  SDL_KillProcess(s->child, true);
  // KillProcess の後で WaitProcess(block=true) を呼んで reap し、その後
  // DestroyProcess するのが SDL3 の推奨。ここでは block して回収する。
  int exit_code = 0;
  SDL_WaitProcess(s->child, true, &exit_code);
  SDL_DestroyProcess(s->child);
  s->child = NULL;
  s->ready = false;
  s->port = 0;
}

bool haxe_server_is_alive(HaxeServer *s) {
  if (!s || !s->child)
    return false;
  int exit_code = 0;
  if (SDL_WaitProcess(s->child, false, &exit_code)) {
    return false; // child has exited
  }
  return true;
}
