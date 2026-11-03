#pragma once
// tcs VM の player の main。
// main を持つ 1 つの .c で include し、LUB_VM_MAIN(config) を書く (config は const LubVmConfig * か NULL)。
//
//   #include <lub/lub_vm_main.h>
//   bool lub_vm_extension_jamo(const LubVmExtensionApi *api, TvmVm *vm);
//   static const LubVmExtensionFn extensions[] = {lub_vm_extension_jamo};
//   static const LubVmConfig config = {extensions, 1};
//   LUB_VM_MAIN(&config)
//
// native の拡張を静的に link するときは、拡張の入口の名前を他と重ならないように変える
// (vm_jamo.c なら -Dlub_vm_extension=lub_vm_extension_jamo で compile する)。
#define SDL_MAIN_USE_CALLBACKS 1
#include <SDL3/SDL_main.h>
#include <lub/lub_vm.h>

#define LUB_VM_MAIN(config)                                                    \
  SDL_AppResult SDL_AppInit(void **appstate, int argc, char **argv) {          \
    (void)appstate;                                                            \
    return lub_vm_app_init(argc, argv, (config));                             \
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
