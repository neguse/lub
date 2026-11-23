#pragma once
// Library for the tcs VM player (src/vm_player.c).
// The stock lub_vm is this plus the few-line main in src/vm_main.c.
// A custom player passes statically linked native extensions from its own main
// (LUB_VM_MAIN in lub_vm_main.h). To use native extensions on the web, build
// the whole player with emscripten in this form. Arguments and behavior (.tcsb
// / .csproj, hot reload, inspection hooks) are the same as the stock lub_vm.
#include <SDL3/SDL.h>
#include <lub/lub_vm_extension.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LubVmConfig {
  // Statically linked native extensions (called each time a VM is created,
  // before the loaded extensions)
  const LubVmExtensionFn *extensions;
  int nextensions;
} LubVmConfig;

// Body of the SDL main callbacks (config may be NULL; it must live until the
// player ends)
SDL_AppResult lub_vm_app_init(int argc, char **argv, const LubVmConfig *config);
SDL_AppResult lub_vm_app_event(SDL_Event *event);
SDL_AppResult lub_vm_app_iterate(void);
void lub_vm_app_quit(SDL_AppResult result);

#ifdef __cplusplus
}
#endif
