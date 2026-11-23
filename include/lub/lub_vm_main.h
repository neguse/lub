#pragma once
// main of the tcs VM player.
// Include this in the one .c file that has main and write LUB_VM_MAIN(config)
// (config is const LubVmConfig * or NULL).
//
//   #include <lub/lub_vm_main.h>
//   bool lub_vm_extension_jamo(const LubVmExtensionApi *api, TvmVm *vm);
//   static const LubVmExtensionFn extensions[] = {lub_vm_extension_jamo};
//   static const LubVmConfig config = {extensions, 1};
//   LUB_VM_MAIN(&config)
//
// When statically linking native extensions, rename each extension's entry
// point so it does not clash with the others (for vm_jamo.c, compile with
// -Dlub_vm_extension=lub_vm_extension_jamo).
#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL_main.h>
#include <lub/lub_vm.h>

#define LUB_VM_MAIN(config)                                                    \
  SDL_AppResult SDL_AppInit(void **appstate, int argc, char **argv) {          \
    (void)appstate;                                                            \
    return lub_vm_app_init(argc, argv, (config));                              \
  }                                                                            \
  SDL_AppResult SDL_AppEvent(void *appstate, SDL_Event *event) {               \
    (void)appstate;                                                            \
    return lub_vm_app_event(event);                                            \
  }                                                                            \
  SDL_AppResult SDL_AppIterate(void *appstate) {                               \
    (void)appstate;                                                            \
    return lub_vm_app_iterate();                                               \
  }                                                                            \
  void SDL_AppQuit(void *appstate, SDL_AppResult result) {                     \
    (void)appstate;                                                            \
    lub_vm_app_quit(result);                                                   \
  }
