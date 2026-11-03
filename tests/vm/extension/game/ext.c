// tests/vm/extension の native の拡張 (scripts/vm-project-test.sh が Ext.so にする)。
#include <lub/lub_vm_extension.h>
#include <stdio.h>
#include <string.h>

static const LubVmExtensionApi *api;

static TvmStatus add(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)user;
  rets[0].i = args[0].i + args[1].i;
  return TVM_OK;
}

// bool Greet(string name, out string? text): 戻り値の葉の後に out の葉
static TvmStatus greet(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  size_t len = 0;
  const char *name = args[0].p ? api->string_data(args[0].p, &len) : NULL;
  if (!name)
    return api->host_fault(vm, "ext.greet: name is null");
  char text[128];
  int n = snprintf(text, sizeof text, "hello %.*s", (int)len, name);
  rets[0].i = 1;
  rets[1].p = api->string_new(vm, text, (size_t)n);
  return TVM_OK;
}

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a, TvmVm *vm) {
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  api = a;
  return a->host(vm, "ext.add", "ii", "i", add, NULL) >= 0 &&
         a->host(vm, "ext.greet", "s", "bs", greet, NULL) >= 0;
}
