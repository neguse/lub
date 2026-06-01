#ifndef LUB_HAXE_SERVER_H
#define LUB_HAXE_SERVER_H

#include <stdbool.h>

typedef struct SDL_Process SDL_Process;

typedef struct HaxeServer {
  SDL_Process *child; // haxe --wait child
  int port;           // 確保した port
  bool ready;         // listening を確認済みなら true
} HaxeServer;

// 起動。`LUB_HAXE_PORT` env var があればそれを 1 回だけ試す。
// 無ければ 7400 から 7410 まで probe。成功時 true。
bool haxe_server_start(HaxeServer *s);

// shutdown。child を kill し、リソース解放。
void haxe_server_stop(HaxeServer *s);

// 子プロセスが死んでいないか確認。死んでいたら false。
bool haxe_server_is_alive(HaxeServer *s);

#endif
