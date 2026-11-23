// The lider player: lub's tcs VM player (library) with lider's native
// extensions (jamo, file system, the tcs compiler) linked statically. Native
// and web (emscripten) are both built from the same main
// (native/CMakeLists.txt). Arguments and behavior are the same as lub's lub_vm
// (lider_player Game.csproj).
#include <lub/lub_vm_main.h>

// native/jamo/vm_jamo.c, native/fs/vm_fs.c, native/tcs/vm_tcs.c (CMake renames
// the entry points)
bool lub_vm_extension_jamo(const LubVmExtensionApi *api, TvmVm *vm);
bool lub_vm_extension_fs(const LubVmExtensionApi *api, TvmVm *vm);
bool lub_vm_extension_tcs(const LubVmExtensionApi *api, TvmVm *vm);

static const LubVmExtensionFn extensions[] = {
    lub_vm_extension_jamo, lub_vm_extension_fs, lub_vm_extension_tcs};
static const LubVmConfig config = {extensions, 3};

LUB_VM_MAIN(&config)
