#pragma once
// Native extensions for the tcs VM player (lub_vm).
// The functions of X.ref.cs placed in the game's source directory (type-only
// declarations that are not compiled; passed with tcs --ref) are registered as
// VM host functions by the native library next to it (X.so, X.dylib, X.dll;
// libX.* if missing). The library exports lub_vm_extension, and the player
// calls it each time it creates a VM (before loading the module). The library
// does not use lub symbols, only the functions in the table passed to it (the
// types are in tcs's tcsvm.h). Host function names are the IL host names
// ("jamo.load_user_data" for LoadUserData of class Jamo).
#include "tcsvm.h"

#define LUB_VM_EXTENSION_VERSION 1
#define LUB_VM_EXTENSION_SYMBOL "lub_vm_extension"

typedef struct LubVmExtensionApi {
  uint32_t version; // LUB_VM_EXTENSION_VERSION
  // 1 if registered, 0 otherwise.
  // The same name may be registered any number of times, and the first
  // registration is used (statically linked extensions come before loaded
  // extensions)
  int (*host)(TvmVm *vm, const char *name, const char *params, const char *rets,
              TvmHostFn fn, void *user);
  TvmStatus (*host_fault)(TvmVm *vm, const char *message);
  void *(*string_new)(TvmVm *vm, const char *data, size_t len);
  const char *(*string_data)(const void *string, size_t *len);
} LubVmExtensionApi;

// true if registration succeeded (if false, the player does not create the VM)
typedef bool (*LubVmExtensionFn)(const LubVmExtensionApi *api, TvmVm *vm);

#if defined(_WIN32)
#define LUB_VM_EXTENSION_EXPORT __declspec(dllexport)
#else
#define LUB_VM_EXTENSION_EXPORT __attribute__((visibility("default")))
#endif
