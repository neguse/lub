#pragma once
#include <stdint.h>
#include <SDL3/SDL.h>
#include "sokol_gfx.h"
#include "lua_api.h"
#include "pass.h"

typedef struct App {
    SDL_Window *window;
    SDL_GLContext gl_ctx;
    LuaCtx lua;
    PassState pass;
    uint64_t frame_index;
} App;

bool app_init(App *app);
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);
