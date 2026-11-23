// The part of the host API (include/lub/lub_host.h) implementation shared with
// the player.
#pragma once
#include "app.h"
#include "lub/lub_host.h"
#include <SDL3/SDL.h>
#include <stdbool.h>

// Copy an SDL event to LubEventData and accumulate input latches (press /
// release edges, mouse motion) in App. true if written to out (false for
// ignored events). quit is returned with kind = LUB_EVENT_KIND_QUIT and also
// sets quit_requested.
bool lub_host_translate_event(App *app, const SDL_Event *e, LubEventData *out);

// Scale from window coordinates (SDL mouse) to framebuffer pixels.
// 1 except for high pixel density windows (metal backend).
float lub_host_pixel_density(Process *proc);

// Rectangle of the main target (framebuffer pixels).
// On devices with a notch or home indicator it is the safe area, and the origin
// for game drawing and input is its top-left corner. Otherwise it is the whole
// window (x = y = 0).
void lub_host_main_rect(Process *proc, int *x, int *y, int *w, int *h);
