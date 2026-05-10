#include "pass.h"
#include "app.h"
#include "sokol_gfx.h"
#include <SDL3/SDL.h>

void pass_state_init(PassState *p) {
    p->in_pass = false;
    p->swapchain_w = 0;
    p->swapchain_h = 0;
    p->app = NULL;
}

void pass_state_set_app(PassState *p, struct App *app) {
    p->app = app;
}

void pass_state_set_swapchain_size(PassState *p, int w, int h) {
    p->swapchain_w = w;
    p->swapchain_h = h;
}

bool pass_state_in_pass(const PassState *p) {
    return p->in_pass;
}

void pass_state_begin_main(PassState *p, float r, float g, float b, float a) {
    if (p->in_pass) {
        SDL_Log("begin_pass called while already in pass (nested passes not supported)");
        return;
    }
    App *app = p->app;
    sg_pass pass = {
        .action.colors[0] = {
            .load_action = SG_LOADACTION_CLEAR,
            .clear_value = {r, g, b, a},
        },
        .swapchain = {
            .width = p->swapchain_w,
            .height = p->swapchain_h,
            .color_format = app_swapchain_color_format(app),
            .depth_format = SG_PIXELFORMAT_DEPTH_STENCIL,
            .sample_count = 1,
            .vulkan = {
                .render_image = (const void*)app->vk_swapchain_images[app->vk_current_image],
                .render_view  = (const void*)app->vk_swapchain_views[app->vk_current_image],
                .depth_stencil_image = (const void*)app->vk_depth_image,
                .depth_stencil_view  = (const void*)app->vk_depth_view,
                .render_finished_semaphore = (const void*)app->vk_present_sem,
                .present_complete_semaphore = (const void*)app->vk_acquire_sem,
            },
        },
    };
    sg_begin_pass(&pass);
    p->in_pass = true;
}

void pass_state_end(PassState *p) {
    if (!p->in_pass) {
        SDL_Log("end_pass called without matching begin_pass");
        return;
    }
    sg_end_pass();
    p->in_pass = false;
}
