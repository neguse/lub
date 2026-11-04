// tests/vm/crash の native の拡張 (scripts/vm-project-test.sh が Crash.so にする)。呼ぶと SIGSEGV で落ちる。
#include <lub/lub_vm_extension.h>
#include <signal.h>

static TvmStatus now(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)args;
  (void)rets;
  (void)user;
  raise(SIGSEGV);
  return TVM_OK;
}

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a,
                                              TvmVm *vm) {
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  return a->host(vm, "crash.now", "", "", now, NULL);
}
