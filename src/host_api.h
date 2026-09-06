// host API (include/lub/lub_host.h) の実装側で player と共有する部分。
#pragma once
#include "app.h"
#include "lub/lub_host.h"
#include <SDL3/SDL.h>
#include <stdbool.h>

// SDL の event を LubEventData に写し、入力の latch (押した / 離した edge、
// mouse の移動量) を App に積む。out に書いたら true (無視する event は
// false)。quit は kind = LUB_EVENT_KIND_QUIT で返し、quit_requested も立てる。
bool lub_host_translate_event(App *app, const SDL_Event *e, LubEventData *out);
