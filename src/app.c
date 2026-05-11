#include "app.h"
#include "backend.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

bool app_init(App *app) {
    memset(app, 0, sizeof(*app));
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!app->window) {
        SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
        return false;
    }
    pass_state_init(&app->pass);
    pass_state_set_app(&app->pass, app);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    capture_state_init(&app->capture);
    app->capture_then_exit = false;
    app->last_w = 0;
    app->last_h = 0;
    app->frame_index = 0;
    app->phase = APP_PHASE_PRE_BACKEND;
    strcpy(app->backend_name, "sokol");
    return true;
}

bool app_backend_init(App *app) {
    if (strcmp(app->backend_name, "sdlgpu") == 0) {
        g_backend = &g_backend_sdlgpu;
    } else {
        g_backend = &g_backend_sokol;
    }
    SDL_Log("backend selected: %s", g_backend->name);
    if (!g_backend->init(app)) {
        SDL_Log("backend init failed");
        return false;
    }
    app->phase = APP_PHASE_POST_BACKEND;
    return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w = 0, h = 0;
    g_backend->begin_frame(app, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
    app->last_w = w;
    app->last_h = h;
}

static void app_on_shader_release(void *ctx, uintptr_t old_shader) {
    App *app = (App*)ctx;
    pipeline_cache_invalidate_shader(&app->pip_cache, old_shader);
}

void app_frame_end(App *app) {
    g_backend->end_frame(app);
    if (capture_state_drain(&app->capture, app)) {
        app->capture_then_exit = true;
    }
    if (app->resource_sweep_after_frames > 0) {
        int64_t cf = (int64_t)app->frame_index;
        int64_t thr = (int64_t)app->resource_sweep_after_frames;
        // Pipelines first: invalidate_shader (called by res sweep) walks the
        // same buckets, so ordering avoids touching freed entries.
        pipeline_cache_sweep(&app->pip_cache, cf, thr);
        res_table_sweep(&app->res, cf, thr, app_on_shader_release, app);
    }
    app->frame_index++;
}

void app_shutdown(App *app) {
    // Pipelines reference shaders, so destroy pipelines before resources.
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    capture_state_shutdown(&app->capture);
    g_backend->shutdown(app);

    if (app->window) SDL_DestroyWindow(app->window);
}
