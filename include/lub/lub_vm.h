#pragma once
// tcs VM の player の library (src/vm_player.c)。
// 既製の lub_vm は、これに src/vm_main.c の数行の main を付けたもの。
// 自前の player は、自分の main (lub_vm_main.h の LUB_VM_MAIN) で、静的に link した native の拡張を渡す。
// web で native の拡張を使うには、この形で player ごと emscripten で build する。
// 引数と動き (.tcsb / .csproj、overlay、hot reload、検査の口) は既製の lub_vm と同じ。
#include <SDL3/SDL.h>
#include <lub/lub_vm_extension.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct LubVmConfig {
  // 静的に link した native の拡張 (VM を作るたびに、読み込んだ拡張より先に呼ぶ)
  const LubVmExtensionFn *extensions;
  int nextensions;
} LubVmConfig;

// SDL の main callback の中身 (config は NULL 可。player の終わりまで生きていること)
SDL_AppResult lub_vm_app_init(int argc, char **argv, const LubVmConfig *config);
SDL_AppResult lub_vm_app_event(SDL_Event *event);
SDL_AppResult lub_vm_app_iterate(void);
void lub_vm_app_quit(SDL_AppResult result);

#ifdef __cplusplus
}
#endif
