#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL.h>
#include <SDL3/SDL_main.h>
#include <stdbool.h>
#include <stdlib.h>
#include <string.h>
#include "app.h"
#include "lua_api.h"
#include "capture.h"
#ifndef __EMSCRIPTEN__
#include "haxe_build.h"  // path_basename_noext / path_dirname
#endif

static App g_app;

static bool has_extension(const char *path, const char *ext) {
    size_t n = SDL_strlen(path), m = SDL_strlen(ext);
    return n >= m && SDL_strcasecmp(path + n - m, ext) == 0;
}

SDL_AppResult SDL_AppInit(void **appstate, int argc, char *argv[]) {
    (void)appstate;
    if (!SDL_Init(SDL_INIT_VIDEO)) {
        SDL_Log("SDL_Init failed: %s", SDL_GetError());
        return SDL_APP_FAILURE;
    }
    if (!app_init(&g_app)) return SDL_APP_FAILURE;

    const char *script        = NULL;
    const char *capture_path  = NULL;
    uint64_t    capture_frame = 30;
    for (int i = 1; i < argc; ++i) {
        if (strcmp(argv[i], "--capture") == 0 && i + 1 < argc) {
            capture_path = argv[++i];
        } else if (strcmp(argv[i], "--capture-frame") == 0 && i + 1 < argc) {
            capture_frame = strtoull(argv[++i], NULL, 10);
        } else {
            script = argv[i];
        }
    }
    const char *entry_path = script ? script : "00_hello";

    // L 作成は entry kind に関わらず先に行う。boot.lua + require の resolution は
    // .hxml dispatch 後に lua_ctx_load_entry で行うことで、package.path 拡張
    // (`<dir>/.lub/?.lua`) が boot.lua の require より先に効くようにする。
    if (!lua_ctx_init(&g_app.lua, &g_app)) return SDL_APP_FAILURE;

    char modbuf[256] = {0};

#ifdef __EMSCRIPTEN__
    if (has_extension(entry_path, ".hxml")) {
        SDL_Log("FATAL: .hxml entry not supported on WASM");
        return SDL_APP_FAILURE;
    }
#else
    if (has_extension(entry_path, ".hxml")) {
        if (!haxe_pipeline_start(&g_app.haxe, entry_path)) {
            SDL_Log("FATAL: haxe pipeline start failed");
            return SDL_APP_FAILURE;
        }
        g_app.haxe_enabled = true;
        // entry module 名は hxml basename
        path_basename_noext(entry_path, modbuf, sizeof(modbuf));
        SDL_snprintf(g_app.entry_module_name, sizeof(g_app.entry_module_name),
                     "%s", modbuf);
        // entry_path 自体は .lub/<base>.lua に向ける (既存 mtime polling が拾う)
        char dir[512];
        path_dirname(entry_path, dir, sizeof(dir));
        char lua_path[768];
        SDL_snprintf(lua_path, sizeof(lua_path), "%s/.lub/%s.lua", dir, modbuf);
        SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path), "%s", lua_path);
        g_app.entry_mtime_cache = 0;
        // package.path 拡張 (boot.lua の require が <dir>/.lub/?.lua を見られる
        // ように)。lua_ctx_add_package_path は内部で `/.lub/?.lua` を付けるので
        // entry dir を渡す。
        lua_ctx_add_package_path(&g_app.lua, dir);
    }
#endif

    if (!has_extension(entry_path, ".hxml")) {
        // Accept "01_triangle", "01_triangle.lua", or "samples/01_triangle.lua"
        // and reduce to the bare module name ("01_triangle") for require().
        const char *raw = entry_path;
        const char *base = strrchr(raw, '/');
        base = base ? base + 1 : raw;
        size_t n = strlen(base);
        if (n >= 4 && strcmp(base + n - 4, ".lua") == 0) n -= 4;
        if (n >= sizeof(modbuf)) n = sizeof(modbuf) - 1;
        memcpy(modbuf, base, n);
        modbuf[n] = '\0';

        SDL_strlcpy(g_app.entry_module_name, modbuf, sizeof(g_app.entry_module_name));
        SDL_snprintf(g_app.entry_path, sizeof(g_app.entry_path),
                     "samples/%s.lua", modbuf);
        g_app.entry_mtime_cache = 0;
    }

    if (!lua_ctx_load_entry(&g_app.lua, modbuf)) return SDL_APP_FAILURE;
    lua_ctx_call_init(&g_app.lua);
    if (g_app.cfg_w > 0 && g_app.cfg_h > 0) {
        SDL_SetWindowSize(g_app.window, g_app.cfg_w, g_app.cfg_h);
    }
    if (!app_backend_init(&g_app)) return SDL_APP_FAILURE;   // initialize GPU backend after Lua onInit

    if (capture_path) {
        capture_schedule(&g_app.capture, capture_path, capture_frame);
        SDL_Log("capture scheduled: path=%s at_frame=%llu",
                capture_path, (unsigned long long)capture_frame);
    }
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {
    (void)appstate;
    if (event->type == SDL_EVENT_QUIT) return SDL_APP_SUCCESS;
    if (event->type == SDL_EVENT_WINDOW_PIXEL_SIZE_CHANGED) {
        g_app.pending_resize = true;
    }
    lua_ctx_call_event(&g_app.lua, event);
    return SDL_APP_CONTINUE;
}

SDL_AppResult SDL_AppIterate(void *appstate) {
    (void)appstate;
    int w, h;
    // Minimized: SDL reports 0x0 pixels and the swapchain can't be (re)created
    // until the window is restored. Don't run the Lua frame callback in that
    // state — the GPU pass would dereference a NULL swapchain image.
    SDL_GetWindowSizeInPixels(g_app.window, &w, &h);
    if (w == 0 || h == 0) {
        SDL_Delay(16);
        return SDL_APP_CONTINUE;
    }
    app_frame_begin(&g_app, &w, &h);
    lua_ctx_call_frame(&g_app.lua);
    // 安全策: onFrame が pass を閉じ忘れた場合、強制的に閉じる
    if (pass_state_in_pass(&g_app.pass)) pass_state_end(&g_app.pass);
    app_frame_end(&g_app);
    if (g_app.quit_requested) return SDL_APP_SUCCESS;
    if (g_app.capture_then_exit) return SDL_APP_SUCCESS;
    return SDL_APP_CONTINUE;
}

void SDL_AppQuit(void *appstate, SDL_AppResult result) {
    (void)appstate; (void)result;
    lua_ctx_call_quit(&g_app.lua);
    lua_ctx_shutdown(&g_app.lua);
    app_shutdown(&g_app);
    SDL_Quit();
}
