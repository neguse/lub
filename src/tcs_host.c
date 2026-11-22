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
#ifdef __ANDROID__
// On Android, the SDL Activity (SDLActivity) calls SDL_main in libmain.so.
// SDL_main.h turns the main below into SDL_main.
#include <SDL3/SDL_main.h>
#endif
#endif
#include <setjmp.h>
#include LUB_TCS_GAME
#include LUB_TCS_BINDING
#ifndef LUB_TCS_ENTRY
#error                                                                         \
    "no entry in binding: pass the game sources (a class with static void OnFrame(float)) to lub-gen tcs, or --entry CLASS; regenerate older bindings"
#endif

// A fault in a callback (tcs_fault) aborts that callback and asks OnError of
// the entry what to do (Log if there is none). If OnError itself faults or
// returns anything other than Log, Abort (exits with exit code 1). A fault
// outside callbacks (static initialization) and a fault inside a callback that
// a runtime function is calling (a query visitor etc.) stop the process.
static jmp_buf tcs_fault_jmp;
static bool tcs_fault_armed;
static const char *tcs_fault_kind;

static void tcs_host_on_fault(const char *kind) {
  if (!tcs_fault_armed || tcs_lub_callback_depth > 0)
    return;
  tcs_fault_armed = false;
  tcs_fault_kind = kind;
  longjmp(tcs_fault_jmp, 1);
}

static void tcs_host_ask_on_error(void) {
  int32_t action = LUB_ERROR_ACTION_ABORT;
  tcs_lib_unwind();
  tcs_fault_armed = true;
  if (setjmp(tcs_fault_jmp) == 0) {
    action = tcs_game_on_error(tcs_fault_kind);
    tcs_fault_armed = false;
  } else {
    tcs_lib_unwind();
  }
  if (action != LUB_ERROR_ACTION_LOG)
    lub_app_quit(tcs_lub_context, 1);
}

typedef enum {
  TCS_HOOK_INIT,
  TCS_HOOK_EVENT,
  TCS_HOOK_FRAME,
  TCS_HOOK_QUIT
} TcsHook;

// Calls one callback.
// true if it finishes without a fault.
static bool tcs_host_call(TcsHook hook, const LubEventData *event, float dt) {
  tcs_fault_armed = true;
  if (setjmp(tcs_fault_jmp) != 0) {
    tcs_host_ask_on_error();
    return false;
  }
  switch (hook) {
  case TCS_HOOK_INIT:
    tcs_game_on_init();
    break;
  case TCS_HOOK_EVENT:
    tcs_game_on_event(event);
    break;
  case TCS_HOOK_FRAME:
    tcs_game_on_frame(dt);
    break;
  case TCS_HOOK_QUIT:
    tcs_game_on_quit();
    break;
  }
  tcs_fault_armed = false;
  return true;
}

#ifdef __EMSCRIPTEN__
static bool tcs_shutting_down;

// Called by the page before removing the player (once removed, SDL_AppQuit is
// not called). Stops the loop, runs OnQuit, then releases the runtime (the SDL
// event handler is removed by SDL_Quit in lub_host_destroy). Host.Send in
// OnQuit is synchronous, so the save message reaches the page before the
// context goes away. The second and later calls, and calls from inside OnQuit
// (the page receives the message and calls this right away), do nothing.
EMSCRIPTEN_KEEPALIVE void lub_tcs_shutdown(void) {
  if (!tcs_lub_context || tcs_shutting_down)
    return;
  tcs_shutting_down = true;
  emscripten_cancel_main_loop();
  tcs_host_call(TCS_HOOK_QUIT, NULL, 0);
  lub_host_destroy(tcs_lub_context);
  tcs_lub_context = NULL;
}

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
  tcs_lib_on_fault(tcs_host_on_fault);
  tcs_lib_init();
  tcs_host_call(TCS_HOOK_INIT, NULL, 0);
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
  tcs_host_call(TCS_HOOK_EVENT, &translated, 0);
  return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
  (void)appstate;
  // Do not run the frame in which OnEvent called Quit
  if (lub_host_quit_requested(tcs_lub_context))
    return SDL_APP_SUCCESS;
  float dt;
  if (lub_host_frame_begin(tcs_lub_context, &dt)) {
    bool ok = tcs_host_call(TCS_HOOK_FRAME, NULL, dt);
    lub_host_frame_end(tcs_lub_context, ok);
  }
  return lub_host_quit_requested(tcs_lub_context) ? SDL_APP_SUCCESS
                                                  : SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
  (void)appstate;
  (void)result;
  lub_tcs_shutdown();
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
  tcs_lib_on_fault(tcs_host_on_fault);
  tcs_lib_init();
  tcs_host_call(TCS_HOOK_INIT, NULL, 0);
  if (lub_host_start(tcs_lub_context) != LUB_OK) {
    lub_host_destroy(tcs_lub_context);
    return 1;
  }
  while (!lub_host_quit_requested(tcs_lub_context)) {
    LubEventData event;
    while (lub_host_poll_event(tcs_lub_context, &event)) {
      if (event.kind == LUB_EVENT_KIND_QUIT)
        break;
      tcs_host_call(TCS_HOOK_EVENT, &event, 0);
    }
    // Do not run the frame that received Quit or the frame in which OnEvent
    // called Quit
    if (lub_host_quit_requested(tcs_lub_context))
      break;
    float dt;
    if (!lub_host_frame_begin(tcs_lub_context, &dt)) {
      SDL_Delay(16);
      continue;
    }
    bool ok = tcs_host_call(TCS_HOOK_FRAME, NULL, dt);
    lub_host_frame_end(tcs_lub_context, ok);
  }
  tcs_host_call(TCS_HOOK_QUIT, NULL, 0);
  int code = lub_host_exit_code(tcs_lub_context);
  lub_host_destroy(tcs_lub_context);
  return code;
}
#endif
