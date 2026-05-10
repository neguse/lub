#include "pass.h"
#include "backend.h"
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
    PassBeginDesc d = {
        .target = 0,
        .clear = { r, g, b, a },
    };
    g_backend->begin_pass(p->app, &d);
    p->in_pass = true;
}

void pass_state_end(PassState *p) {
    if (!p->in_pass) {
        SDL_Log("end_pass called without matching begin_pass");
        return;
    }
    g_backend->end_pass(p->app);
    p->in_pass = false;
}
