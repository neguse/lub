// lider の player: lub の tcs VM の player (library) に、lider の native の拡張 (jamo、file system) を静的に link したもの。
// native も web (emscripten) も同じ main で build する (native/CMakeLists.txt)。
// 引数と動きは lub の lub_vm と同じ (lider_player Game.csproj)。
#include <lub/lub_vm_main.h>

// native/jamo/vm_jamo.c と native/fs/vm_fs.c (入口の名前は CMake が変える)
bool lub_vm_extension_jamo(const LubVmExtensionApi *api, TvmVm *vm);
bool lub_vm_extension_fs(const LubVmExtensionApi *api, TvmVm *vm);

static const LubVmExtensionFn extensions[] = {lub_vm_extension_jamo,
                                              lub_vm_extension_fs};
static const LubVmConfig config = {extensions, 2};

LUB_VM_MAIN(&config)
