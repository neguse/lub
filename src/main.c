#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include "app.h"
#include "lua_api.h"

static App g_app;

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
    (void)appstate;
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        SDL_Log("SDL_Init failed: %s", SDL_GetError());
        return SDL_APP_FAILURE;
    }
    if (!app_init(&g_app)) return SDL_APP_FAILURE;
    const char *script = (argc >= 2) ? argv[1] : "samples/00_hello.lua";
    if (!lua_ctx_init(&g_app.lua, script)) return SDL_APP_FAILURE;
    lua_ctx_call_init(&g_app.lua);
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
    (void)appstate;
    if (event->type == SDL_EVENT_QUIT) return SDL_APP_SUCCESS;
    lua_ctx_call_event(&g_app.lua, event);
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    int w, h;
    app_frame_begin(&g_app, &w, &h);
    sg_pass pass = {
        .action = {
            .colors[0] = { .load_action = SG_LOADACTION_CLEAR,
                           .clear_value = { 0.1f, 0.15f, 0.25f, 1.0f } },
        },
        .swapchain = {
            .width = w, .height = h,
            .sample_count = 1,
            .color_format = SG_PIXELFORMAT_RGBA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .gl.framebuffer = 0,
        },
    };
    sg_begin_pass(&pass);
    sg_end_pass();
    lua_ctx_call_frame(&g_app.lua);
    app_frame_end(&g_app);
    return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
    (void)appstate; (void)result;
    lua_ctx_call_quit(&g_app.lua);
    lua_ctx_shutdown(&g_app.lua);
    app_shutdown(&g_app);
    SDL_Quit();
}
