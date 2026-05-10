#include "app.h"
#include "sokol_gfx.h"
#include <SDL3/SDL.h>

bool app_init(App *app) {
    app->window = SDL_CreateWindow("sglua", 1280, 720,
        SDL_WINDOW_VULKAN | SDL_WINDOW_RESIZABLE);
    if (!app->window) {
        SDL_Log("SDL_CreateWindow failed: %s", SDL_GetError());
        return false;
    }
    pass_state_init(&app->pass);
    res_table_init(&app->res);
    pipeline_cache_init(&app->pip_cache);
    app->frame_index = 0;
    // Vulkan init happens in Task 14
    return false;  // Task 14 makes this return true
}

void app_frame_begin(App *app, int *out_w, int *out_h) {
    int w, h;
    SDL_GetWindowSizeInPixels(app->window, &w, &h);
    if (out_w) *out_w = w;
    if (out_h) *out_h = h;
    pass_state_set_swapchain_size(&app->pass, w, h);
}

void app_frame_end(App *app) {
    // Task 14/15 add sg_commit and vkQueuePresentKHR
    app->frame_index++;
}

void app_shutdown(App *app) {
    // Pipelines reference shaders, so destroy pipelines before resources.
    pipeline_cache_shutdown(&app->pip_cache);
    res_table_shutdown(&app->res);
    if (app->window) SDL_DestroyWindow(app->window);
}
