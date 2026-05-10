#pragma once
#include <stdbool.h>

struct App; // 前方宣言

typedef struct PassState {
    bool in_pass;
    struct App *app;     // backend pass calls need the App context
} PassState;

void pass_state_init(PassState *p);
void pass_state_set_app(PassState *p, struct App *app);
bool pass_state_in_pass(const PassState *p);
void pass_state_begin_main(PassState *p, float r, float g, float b, float a);
void pass_state_end(PassState *p);
