/* Runs the tcs CLI on the tcs VM: loads the .tcsb of tcs itself (tcs vm
   --export 'Tcs.Cli.*' of src/tcs/sources.sh) and gives it what it asks of
   the outside as VM host functions, on the same OS file functions as the C tcs
   (src/tcs/Native/files.c).
     tcs_on_vm <tcs.tcsb> <tcs arguments>...
   Like the C tcs, it gives each argument to Cli.Arg and calls Cli.Step until it
   returns the exit code, with a frame boundary after every call.
   The files shipped with tcs (lib/, runtime/) are read from the dir in the
   TCS_HOME environment variable or from the dir of the .tcsb.
   If TCS_HEAP=1, prints to stderr the maximum over the steps of the bytes alive
   after the VM's last collection. */
#include "files.c"
#include "tcsvm.h"

#include <time.h>

static char *shipped_home;

/* The VM string as a C string (malloc'd) */
static char *cstring(void *string) {
  size_t len = 0;
  const char *data = string ? tvm_string_data(string, &len) : "";
  char *p = tcs_files_alloc(len + 1);
  memcpy(p, data, len);
  p[len] = 0;
  return p;
}

/* The text of a file as a VM string. NULL (null) if it cannot be read */
static void *read_string(TvmVm *vm, const char *path) {
  size_t length;
  char *buf = tcs_files_read(path, &length);
  if (buf == NULL)
    return NULL;
  void *text = tvm_string_new(vm, buf, length);
  free(buf);
  return text;
}

static TvmStatus f_read(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  char *path = cstring(args[0].p);
  rets[0].p = read_string(vm, path);
  free(path);
  return TVM_OK;
}

static TvmStatus f_shipped(TvmVm *vm, TvmSlot *args, TvmSlot *rets,
                           void *user) {
  (void)user;
  char *name = cstring(args[0].p);
  size_t n = strlen(shipped_home) + strlen(name) + 1;
  char *full = tcs_files_alloc(n);
  snprintf(full, n, "%s%s", shipped_home, name);
  free(name);
  rets[0].p = read_string(vm, full);
  free(full);
  return TVM_OK;
}

static TvmStatus f_same_file(TvmVm *vm, TvmSlot *args, TvmSlot *rets,
                             void *user) {
  (void)vm;
  (void)user;
  char *a = cstring(args[0].p);
  char *b = cstring(args[1].p);
  rets[0].i = tcs_files_same(a, b) != 0;
  free(a);
  free(b);
  return TVM_OK;
}

static TvmStatus f_stamp(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  char *path = cstring(args[0].p);
  char stamp[96];
  tcs_files_stamp(path, stamp);
  free(path);
  rets[0].p = tvm_string_new(vm, stamp, strlen(stamp));
  return TVM_OK;
}

/* A VM string is a byte sequence, so text and bytes are written the same way */
static TvmStatus f_write(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  char *path = cstring(args[0].p);
  size_t len = 0;
  const char *data = args[1].p ? tvm_string_data(args[1].p, &len) : "";
  char *failure = tcs_files_write(path, data, len);
  free(path);
  rets[0].p = tvm_string_new(vm, failure ? failure : "",
                             failure ? strlen(failure) : 0);
  free(failure);
  return TVM_OK;
}

static TvmStatus f_out(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)rets;
  size_t len = 0;
  const char *data = args[0].p ? tvm_string_data(args[0].p, &len) : "";
  fwrite(data, 1, len, (FILE *)user);
  return TVM_OK;
}

static TvmStatus f_flush(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)args;
  (void)rets;
  (void)user;
  fflush(stdout);
  return TVM_OK;
}

static TvmStatus f_sleep(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)rets;
  (void)user;
#ifdef _WIN32
  Sleep((DWORD)args[0].i);
#else
  struct timespec ts;
  ts.tv_sec = args[0].i / 1000;
  ts.tv_nsec = (long)(args[0].i % 1000) * 1000000L;
  nanosleep(&ts, NULL);
#endif
  return TVM_OK;
}

static int fail(TvmVm *vm, const char *what) {
  fprintf(stderr, "tcs_on_vm: %s: %s\n", what, tvm_error(vm));
  return 2;
}

int main(int argc, char **argv) {
  if (argc < 2) {
    fputs("usage: tcs_on_vm <tcs.tcsb> <tcs arguments>...\n", stderr);
    return 2;
  }
  size_t size;
  FILE *f = fopen(argv[1], "rb");
  if (f == NULL) {
    fprintf(stderr, "tcs_on_vm: cannot read %s\n", argv[1]);
    return 2;
  }
  fseek(f, 0, SEEK_END);
  size = (size_t)ftell(f);
  fseek(f, 0, SEEK_SET);
  uint8_t *bytes = tcs_files_alloc(size > 0 ? size : 1);
  if (fread(bytes, 1, size, f) != size) {
    fprintf(stderr, "tcs_on_vm: cannot read %s\n", argv[1]);
    return 2;
  }
  fclose(f);
  const char *env = getenv("TCS_HOME");
  if (env != NULL && env[0] != 0)
    shipped_home = tcs_files_home(NULL);
  else {
    /* The dir of the .tcsb stands in for the dir of the executable */
    size_t cut = strlen(argv[1]);
    while (cut > 0 && !tcs_files_is_sep(argv[1][cut - 1]))
      cut--;
    shipped_home = tcs_files_alloc(cut + 1);
    memcpy(shipped_home, argv[1], cut);
    shipped_home[cut] = 0;
  }

  TvmVm *vm = tvm_new(NULL);
  int ok = tvm_host(vm, "tcsfiles.read", "s", "s", f_read, NULL) &&
           tvm_host(vm, "tcsfiles.shipped", "s", "s", f_shipped, NULL) &&
           tvm_host(vm, "tcsfiles.same_file", "ss", "b", f_same_file, NULL) &&
           tvm_host(vm, "tcsfiles.stamp", "s", "s", f_stamp, NULL) &&
           tvm_host(vm, "tcsfiles.write_text", "ss", "s", f_write, NULL) &&
           tvm_host(vm, "tcsfiles.write_bytes", "ss", "s", f_write, NULL) &&
           tvm_host(vm, "cliprocess.out", "s", "", f_out, stdout) &&
           tvm_host(vm, "cliprocess.err", "s", "", f_out, stderr) &&
           tvm_host(vm, "cliprocess.flush", "", "", f_flush, NULL) &&
           tvm_host(vm, "cliprocess.sleep", "i", "", f_sleep, NULL);
  if (!ok) {
    fputs("tcs_on_vm: cannot register the host functions\n", stderr);
    return 2;
  }
  if (tvm_load(vm, bytes, size) != TVM_OK)
    return fail(vm, "load");
  int32_t arg = tvm_find(vm, "Tcs_Cli.Arg");
  int32_t step = tvm_find(vm, "Tcs_Cli.Step");
  if (arg < 0 || step < 0) {
    fputs("tcs_on_vm: the module has no Tcs_Cli.Arg or Tcs_Cli.Step\n", stderr);
    return 2;
  }
  for (int i = 2; i < argc; i++) {
    TvmSlot a;
    a.p = tvm_string_new(vm, argv[i], strlen(argv[i]));
    if (tvm_call(vm, arg, &a, 1, NULL, 0) != TVM_OK)
      return fail(vm, "Cli.Arg");
    tvm_frame_end(vm);
  }
  const char *heap = getenv("TCS_HEAP");
  size_t peak = 0;
  int steps = 0;
  TvmSlot code;
  do {
    code.i = 0;
    if (tvm_call(vm, step, NULL, 0, &code, 1) != TVM_OK)
      return fail(vm, "Cli.Step");
    tvm_frame_end(vm);
    steps++;
    size_t now = tvm_gc_live(vm);
    if (now > peak)
      peak = now;
  } while (code.i < 0);
  if (heap != NULL && heap[0] == '1')
    fprintf(stderr, "steps %d, peak managed heap between steps %zu bytes\n",
            steps, peak);
  fflush(stdout);
  return code.i;
}
