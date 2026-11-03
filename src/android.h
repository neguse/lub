#pragma once
#include <stdbool.h>

// Android の player の起動時の準備 (src/android.c)。
// SDL_Init より前に lub_host_create が呼ぶ。
bool android_prepare(void);
