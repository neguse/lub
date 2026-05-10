#pragma once
#include <stdint.h>
#include <SDL3/SDL.h>
#include "lua_api.h"
#include "pass.h"
#include "resources.h"
#include "pipeline.h"

typedef struct App {
    SDL_Window *window;
    // Vulkan handles added in Task 14
    LuaCtx lua;
    PassState pass;
    ResTable res;
    PipelineCache pip_cache;
    uint64_t frame_index;
} App;

bool app_init(App *app);
void app_frame_begin(App *app, int *out_w, int *out_h);
void app_frame_end(App *app);
void app_shutdown(App *app);
