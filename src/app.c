#include "app.h"
#include <SDL3/SDL.h>

static void sglua_sokol_logger(const char *tag, uint32_t log_level, uint32_t log_item_id,
                               const char *message_or_null, uint32_t line_nr,
                               const char *filename_or_null, void *user_data) {
    (void)user_data;
    SDL_Log("sokol[%s] level=%u item=%u %s:%u: %s",
            tag ? tag : "", log_level, log_item_id,
            filename_or_null ? filename_or_null : "(no file)",
            line_nr, message_or_null ? message_or_null : "(no msg)");
}

bool app_init(App *app) {
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MAJOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_MINOR_VERSION, 3);
    SDL_GL_SetAttribute(SDL_GL_CONTEXT_PROFILE_MASK, SDL_GL_CONTEXT_PROFILE_CORE);
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_OPENGL | SDL_WINDOW_RESIZABLE);
    if (!app->window) {
        SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
        return false;
    }
    app->gl_ctx = SDL_GL_CreateContext(app->window);
    if (!app->gl_ctx) {
        SDL_Log("SDL_GL_CreateContext failed: %s", SDL_GetError());
        SDL_DestroyWindow(app->window);
        app->window = NULL;
        return false;
    }
    SDL_GL_MakeCurrent(app->window, app->gl_ctx);
    SDL_GL_SetSwapInterval(1);

    sg_setup(&(sg_desc){
        .environment = { .defaults = {
            .color_format = SG_PIXELFORMAT_RGBA8,
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
        }},
        .logger.func = sglua_sokol_logger,
    });
    pass_state_init(&app->pass);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    app->frame_index = 0;
    return true;
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    pass_state_set_swapchain_size(&app->pass, w, h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
}

void app_frame_end(App *app) {
    sg_commit();
    SDL_GL_SwapWindow(app->window);
    app->frame_index++;
}

void app_shutdown(App *app) {
    // Pipelines reference shaders, so destroy pipelines before resources.
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    sg_shutdown();
    if (app->gl_ctx) SDL_GL_DestroyContext(app->gl_ctx);
    if (app->window) SDL_DestroyWindow(app->window);
}
