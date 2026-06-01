#include "../../src/haxe_server.h"
#include <SDL3/SDL.h>
#include <stdio.h>

int main(void) {
  HaxeServer s;
  if (!haxe_server_start(&s)) {
    SDL_Log("haxe_server_start failed");
    return 1;
  }
  SDL_Log("haxe --wait running on port %d", s.port);
  if (!haxe_server_is_alive(&s)) {
    SDL_Log("server died immediately");
    haxe_server_stop(&s);
    return 1;
  }
  SDL_Delay(500);
  if (!haxe_server_is_alive(&s)) {
    SDL_Log("server died after 500ms");
    haxe_server_stop(&s);
    return 1;
  }
  haxe_server_stop(&s);
  return 0;
}
