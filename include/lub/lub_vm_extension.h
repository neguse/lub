#pragma once
// tcs VM の player (lub_vm) の native の拡張。
// ゲームのソースのディレクトリに置いた X.ref.cs (変換しない型だけの宣言。tcs の --ref で渡る) の関数を、隣の native の library (X.so、X.dylib、X.dll。無ければ libX.*) が VM の host の関数として登録する。
// library は lub_vm_extension を export し、player は VM を作るたび (module を読む前) にそれを呼ぶ。
// library は lub の symbol を使わず、渡した表の関数だけを使う (型は tcs の tcsvm.h)。
// host の関数の名前は IL の host の名前 (class Jamo の LoadUserData なら "jamo.load_user_data")。
#include "tcsvm.h"

#define LUB_VM_EXTENSION_VERSION 1
#define LUB_VM_EXTENSION_SYMBOL "lub_vm_extension"

typedef struct LubVmExtensionApi {
  uint32_t version; // LUB_VM_EXTENSION_VERSION
  int (*host)(TvmVm *vm, const char *name, const char *params,
              const char *rets, TvmHostFn fn, void *user);
  TvmStatus (*host_fault)(TvmVm *vm, const char *message);
  void *(*string_new)(TvmVm *vm, const char *data, size_t len);
  const char *(*string_data)(const void *string, size_t *len);
} LubVmExtensionApi;

// 登録できたら true (false なら player は VM を作らない)
typedef bool (*LubVmExtensionFn)(const LubVmExtensionApi *api, TvmVm *vm);

#if defined(_WIN32)
#define LUB_VM_EXTENSION_EXPORT __declspec(dllexport)
#else
#define LUB_VM_EXTENSION_EXPORT __attribute__((visibility("default")))
#endif
