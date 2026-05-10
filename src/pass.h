#pragma once
#include <stdbool.h>

struct App; // 前方宣言

typedef struct PassState {
    bool in_pass;
    int swapchain_w, swapchain_h;
    struct App *app;     // swapchain handle 取得用
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
void pass_state_set_swapchain_size(PassState *p, int w, int h);
bool pass_state_in_pass(const PassState *p);
void pass_state_begin_main(PassState *p, float r, float g, float b, float a);
void pass_state_end(PassState *p);
