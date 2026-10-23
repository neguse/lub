#ifdef __EMSCRIPTEN__
#define SDL_MAIN_USE_CALLBACKS 1
#include "api_internal.h"
#include "host_api.h"
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <emscripten.h>
#include <lub/lub_host.h>
#else
#include <SDL3/SDL_timer.h>
#include <lub/lub_host.h>
#endif
#include LUB_TCS_GAME
#include LUB_TCS_BINDING
#ifndef LUB_TCS_ENTRY
#error                                                                         \
    "no entry in binding: pass the game sources (a class with static void OnFrame(float)) to lub-gen tcs, or --entry CLASS; regenerate older bindings"
#endif

#ifdef __EMSCRIPTEN__
EMSCRIPTEN_KEEPALIVE void lub_tcs_volume(float volume) {
  if (tcs_lub_context)
    lub_audio_master_volume(tcs_lub_context, volume);
}

SDL_AppResult SDL_AppInit(void **appstate, int argc, char **argv) {
  (void)appstate;
  (void)argc;
  (void)argv;
  LubHostOpts opts = {0};
  tcs_lub_context = lub_host_create(&opts);
  if (!tcs_lub_context)
    return SDL_APP_FAILURE;
  tcs_lib_init();
  tcs_game_on_init();
  return lub_host_start(tcs_lub_context) == LUB_OK ? SDL_APP_CONTINUE
                                                   : SDL_APP_FAILURE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
  (void)appstate;
  LubEventData translated;
  if (!lub_host_translate_event(lub_api_app(tcs_lub_context), event,
                                &translated))
    return SDL_APP_CONTINUE;
  if (translated.kind == LUB_EVENT_KIND_QUIT)
    return SDL_APP_SUCCESS;
  tcs_game_on_event(&translated);
  return SDL_APP_CONTINUE;
}

// tcs2c のゲームの error (tcs_fault) は process を止めるので、frame_end に届く frame は常に成功している。
SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
  // OnEvent が Quit を呼んだ frame は回さない
  if (lub_host_quit_requested(tcs_lub_context))
    return SDL_APP_SUCCESS;
  float dt;
  if (lub_host_frame_begin(tcs_lub_context, &dt)) {
    tcs_game_on_frame(dt);
    lub_host_frame_end(tcs_lub_context, true);
  }
  return lub_host_quit_requested(tcs_lub_context) ? SDL_APP_SUCCESS
                                                  : SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  (void)result;
  if (tcs_lub_context) {
    tcs_game_on_quit();
    lub_host_destroy(tcs_lub_context);
    tcs_lub_context = NULL;
  }
}
#else
int main(int argc, char **argv) {
  LubHostOpts opts = {0};
  for (int i = 1; i < argc; i++) {
    if (!strcmp(argv[i], "--digest"))
      opts.digest = true;
    else if (!strcmp(argv[i], "--fixed-dt") && i + 1 < argc)
      opts.fixed_dt = strtof(argv[++i], NULL);
    else if (!strcmp(argv[i], "--capture-frame") && i + 1 < argc)
      opts.capture_frame = atoi(argv[++i]);
    else if (!strcmp(argv[i], "--capture") && i + 1 < argc) {
      const char *path = argv[++i];
      opts.capture_path = (LubStr){path, (int32_t)strlen(path)};
    } else {
      fprintf(stderr, "Unknown argument: %s\n", argv[i]);
      return 2;
    }
  }
  tcs_lub_context = lub_host_create(&opts);
  if (!tcs_lub_context)
    return 1;
  tcs_lib_init();
  tcs_game_on_init();
  if (lub_host_start(tcs_lub_context) != LUB_OK) {
    lub_host_destroy(tcs_lub_context);
    return 1;
  }
  while (!lub_host_quit_requested(tcs_lub_context)) {
    LubEventData event;
    while (lub_host_poll_event(tcs_lub_context, &event)) {
      if (event.kind == LUB_EVENT_KIND_QUIT)
        break;
      tcs_game_on_event(&event);
    }
    // Quit を受けた frame と OnEvent が Quit を呼んだ frame は回さない
    if (lub_host_quit_requested(tcs_lub_context))
      break;
    float dt;
    if (!lub_host_frame_begin(tcs_lub_context, &dt)) {
      SDL_Delay(16);
      continue;
    }
    tcs_game_on_frame(dt);
    lub_host_frame_end(tcs_lub_context, true);
  }
  tcs_game_on_quit();
  lub_host_destroy(tcs_lub_context);
  return 0;
}
#endif
