/* Exposes the tcs compiler (the functions in src/Tcs.ref.cs) as a native
 * extension of the tcs VM player. Includes tcs.c, which is tcs (Compiler,
 * CBackend, VmBackend, Api and Cli in src/tcs) converted to C with `tcs c`,
 * and implements what tcs asks of the outside: files with the functions shared
 * with the tcs CLI (src/tcs/Native/files_tcs.c), and the rest here.
 * Requests follow src/tcs/Cli/Sessions.cs: the words of a request are given
 * with Tcs.Arg, Tcs.Call processes the request, and the reply lines are
 * received. On the web (emscripten) the files are virtual files (MEMFS).
 * The lider player (native/CMakeLists.txt) links it statically (the entry
 * point is named lub_vm_extension_tcs). lub's stock lub_vm loads src/Tcs.so
 * built by scripts/build-native.sh. */
#include "tcs.c"

#include "files_tcs.c"
#include "tcs_tool_files.h"
#include <lub/lub_vm_extension.h>

static const LubVmExtensionApi *api;

/* What Sessions.Call wrote: the reply of the request */
static char *reply;
static size_t reply_length;
static size_t reply_capacity;

/* Files shipped with tcs (lib/TinySystem/*.cs). Embedded at build time
 * (tcs_tool_files.h) */
TcsString *tcs_host_tcsfiles_shipped(TcsString *name) {
  for (size_t i = 0; i < sizeof tcs_tool_files / sizeof tcs_tool_files[0];
       i++) {
    const TcsToolFile *t = &tcs_tool_files[i];
    if (strlen(t->name) == name->length &&
        memcmp(t->name, name->data, name->length) == 0)
      return tcs_files_string((const char *)t->data, t->length);
  }
  return NULL;
}

void tcs_host_cliprocess_out(TcsString *text) {
  if (reply_length + text->length + 1 > reply_capacity) {
    size_t cap = reply_capacity ? reply_capacity : 4096;
    while (cap < reply_length + text->length + 1)
      cap *= 2;
    reply = realloc(reply, cap);
    if (reply == NULL)
      tcs_fault("out-of-memory");
    reply_capacity = cap;
  }
  memcpy(reply + reply_length, text->data, text->length);
  reply_length += text->length;
  reply[reply_length] = 0;
}

void tcs_host_cliprocess_err(TcsString *text) {
  fwrite(text->data, 1, text->length, stderr);
}

void tcs_host_cliprocess_flush(void) {}

void tcs_host_cliprocess_sleep(int32_t milliseconds) { (void)milliseconds; }

/* Gives one word of a request (a VM string) to tcs. Returning from the entry
 * is a GC frame boundary of the C runtime */
static TvmStatus f_arg(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)rets;
  (void)user;
  size_t len = 0;
  const char *data = args[0].p ? api->string_data(args[0].p, &len) : "";
  tcs_entry_Tcs_Sessions_Arg(tcs_files_string(data, len));
  return TVM_OK;
}

/* Processes the request made of the words given so far and returns the reply */
static TvmStatus f_call(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  reply_length = 0;
  tcs_entry_Tcs_Sessions_Call();
  *(void **)args[0].p = api->string_new(vm, reply ? reply : "", reply_length);
  rets[0].i = 1;
  return TVM_OK;
}

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a,
                                              TvmVm *vm) {
  static bool started;
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  api = a;
  /* The player calls this each time it creates a VM. There is one tcs state
   * (open sessions) per process */
  if (!started) {
    tcs_lib_init();
    started = true;
  }
  return a->host(vm, "tcs.arg", "s", "", f_arg, NULL) &&
         a->host(vm, "tcs.call", "&s", "b", f_call, NULL);
}
