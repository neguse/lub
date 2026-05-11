#pragma once
#include <stdbool.h>
#include <stdint.h>
#include "enums.h"

struct App; // 前方宣言

typedef struct PassState {
    bool in_pass;
    struct App *app;     // backend pass calls need the App context
    SglPixelFormat current_color_fmt; // valid while in_pass; pipeline cache key input
    bool current_has_depth;           // valid while in_pass; pipeline cache key input
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
bool pass_state_in_pass(const PassState *p);
// target_image = 0 -> main_tex (swapchain). target_w/h are ignored when target_image == 0.
void pass_state_begin(PassState *p, uintptr_t target_image, SglPixelFormat fmt,
                      int target_w, int target_h,
                      float r, float g, float b, float a);
void pass_state_end(PassState *p);
