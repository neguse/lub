#pragma once
#include <stdbool.h>

// Startup preparation for the Android player (src/android.c).
// Called by lub_host_create before SDL_Init.
bool android_prepare(void);
