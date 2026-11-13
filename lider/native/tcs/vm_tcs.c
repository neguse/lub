/* tcs の compiler (src/Tcs.ref.cs の関数) を、tcs VM の player の native の拡張として出す。
 * tcs (tcs/SelfHost の Compiler、CBackend、VmBackend、Tool) を tcs2c で C にした tcs.c を include し、tcs の host の関数 (tcs/SelfHost/Tool/TcsHost.cs) を実装する。
 * 要求は tcs/SelfHost/Tool/Sessions.cs の形で、Tcs.Arg で引数を並べてから Tcs.Call で処理し、応答の行を受け取る。
 * file の読み書きは C の標準の関数で、web (emscripten) では仮想ファイル (MEMFS) が相手。
 * lider の player (native/CMakeLists.txt) は静的に link する (入口の名前は lub_vm_extension_tcs)。
 * lub の既製の lub_vm は、scripts/build-native.sh が作る src/Tcs.so を読む。 */
#include "tcs.c"

#include "tcs_tool_files.h"
#include <lub/lub_vm_extension.h>
#include <string.h>
#include <sys/stat.h>
#ifdef _WIN32
#include <direct.h>
#else
#include <unistd.h>
#endif

static const LubVmExtensionApi *api;

typedef struct HostBytes {
  char *data;
  size_t length;
} HostBytes;

static HostBytes *host_args;
static int host_arg_count;
static int host_arg_capacity;
static char *host_out;
static size_t host_out_length;
static size_t host_out_capacity;

static TcsString *host_string(const char *data, size_t length) {
  return tcs_string_new((const unsigned char *)data, length);
}

static char *host_cstring(TcsString *s) {
  char *out = malloc(s->length + 1);
  if (out == NULL)
    tcs_fault("out-of-memory");
  memcpy(out, s->data, s->length);
  out[s->length] = 0;
  return out;
}

static TcsString *host_error(const char *what, const char *path) {
  size_t n = strlen(what) + strlen(path) + 3;
  char *text = malloc(n);
  if (text == NULL)
    tcs_fault("out-of-memory");
  snprintf(text, n, "%s: %s", what, path);
  TcsString *s = host_string(text, strlen(text));
  free(text);
  return s;
}

/* path の親のディレクトリを作る */
static void host_mkdirs(const char *path) {
  char *dir = strdup(path);
  if (dir == NULL)
    tcs_fault("out-of-memory");
  for (char *p = dir + 1; *p; p++) {
    if (*p != '/' && *p != '\\')
      continue;
    char c = *p;
    *p = 0;
#ifdef _WIN32
    _mkdir(dir);
#else
    mkdir(dir, 0777);
#endif
    *p = c;
  }
  free(dir);
}

int32_t tcs_host_tcshost_arg_count(void) { return host_arg_count; }

TcsString *tcs_host_tcshost_arg(int32_t index) {
  return host_string(host_args[index].data, host_args[index].length);
}

bool tcs_host_tcshost_file_exists(TcsString *path) {
  char *name = host_cstring(path);
  struct stat st;
  bool found = stat(name, &st) == 0 && (st.st_mode & S_IFMT) == S_IFREG;
  free(name);
  return found;
}

bool tcs_host_tcshost_same_file(TcsString *a, TcsString *b) {
  char *x = host_cstring(a);
  char *y = host_cstring(b);
  struct stat sx, sy;
  bool same = stat(x, &sx) == 0 && stat(y, &sy) == 0;
#ifdef _WIN32
  same = same && strcmp(x, y) == 0;
#else
  same = same && sx.st_dev == sy.st_dev && sx.st_ino == sy.st_ino;
#endif
  free(x);
  free(y);
  return same;
}

TcsString *tcs_host_tcshost_read_file(TcsString *path) {
  char *name = host_cstring(path);
  FILE *f = fopen(name, "rb");
  free(name);
  if (f == NULL)
    return host_string("", 0);
  fseek(f, 0, SEEK_END);
  long size = ftell(f);
  fseek(f, 0, SEEK_SET);
  char *data = malloc(size > 0 ? (size_t)size : 1);
  if (data == NULL)
    tcs_fault("out-of-memory");
  size_t got = size > 0 ? fread(data, 1, (size_t)size, f) : 0;
  fclose(f);
  TcsString *text = host_string(data, got);
  free(data);
  return text;
}

/* 同じディレクトリの一時ファイルに書いてから置き換える。失敗したら理由 (成功は "") */
TcsString *tcs_host_tcshost_write_file_atomic(TcsString *path,
                                              TcsString *text) {
  char *name = host_cstring(path);
  host_mkdirs(name);
  size_t n = strlen(name) + 5;
  char *temp = malloc(n);
  if (temp == NULL)
    tcs_fault("out-of-memory");
  snprintf(temp, n, "%s.tmp", name);
  TcsString *failure = host_string("", 0);
  FILE *f = fopen(temp, "wb");
  if (f == NULL)
    failure = host_error("cannot write", temp);
  else {
    bool ok = fwrite(text->data, 1, text->length, f) == text->length;
    ok = fclose(f) == 0 && ok;
#ifdef _WIN32
    remove(name);
#endif
    if (!ok || rename(temp, name) != 0) {
      remove(temp);
      failure = host_error("cannot write", name);
    }
  }
  free(temp);
  free(name);
  return failure;
}

TcsString *tcs_host_tcshost_write_binary_atomic(TcsString *path,
                                                TcsString *bytes) {
  return tcs_host_tcshost_write_file_atomic(path, bytes);
}

void tcs_host_tcshost_write_out(TcsString *text) {
  if (host_out_length + text->length + 1 > host_out_capacity) {
    size_t cap = host_out_capacity ? host_out_capacity : 4096;
    while (cap < host_out_length + text->length + 1)
      cap *= 2;
    host_out = realloc(host_out, cap);
    if (host_out == NULL)
      tcs_fault("out-of-memory");
    host_out_capacity = cap;
  }
  memcpy(host_out + host_out_length, text->data, text->length);
  host_out_length += text->length;
  host_out[host_out_length] = 0;
}

void tcs_host_tcshost_write_err(TcsString *text) {
  fwrite(text->data, 1, text->length, stderr);
}

void tcs_host_tcshost_flush_out(void) {}

TcsString *tcs_host_tcshost_file_stamp(TcsString *path) {
  char *name = host_cstring(path);
  struct stat st;
  char stamp[64] = "";
  if (stat(name, &st) == 0)
    snprintf(stamp, sizeof stamp, "%lld:%lld", (long long)st.st_mtime,
             (long long)st.st_size);
  free(name);
  return host_string(stamp, strlen(stamp));
}

/* tcs と一緒に配るファイル (lib/TinySystem/*.cs)。build のときに埋め込む (tcs_tool_files.h) */
TcsString *tcs_host_tcshost_tool_file(TcsString *name) {
  for (size_t i = 0; i < sizeof tcs_tool_files / sizeof tcs_tool_files[0];
       i++) {
    const TcsToolFile *t = &tcs_tool_files[i];
    if (strlen(t->name) == name->length &&
        memcmp(t->name, name->data, name->length) == 0)
      return host_string((const char *)t->data, t->length);
  }
  return host_string("", 0);
}

void tcs_host_tcshost_sleep(int32_t milliseconds) { (void)milliseconds; }

void tcs_host_tcshost_set_exit_code(int32_t code) { (void)code; }

void tcs_host_tcshost_set_done(void) {}

/* VM の string を引数の末尾に足す */
static TvmStatus f_arg(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)rets;
  (void)user;
  size_t len = 0;
  const char *data = args[0].p ? api->string_data(args[0].p, &len) : "";
  if (host_arg_count == host_arg_capacity) {
    host_arg_capacity = host_arg_capacity ? host_arg_capacity * 2 : 16;
    host_args =
        realloc(host_args, sizeof(HostBytes) * (size_t)host_arg_capacity);
    if (host_args == NULL)
      tcs_fault("out-of-memory");
  }
  HostBytes b = {malloc(len > 0 ? len : 1), len};
  if (b.data == NULL)
    tcs_fault("out-of-memory");
  memcpy(b.data, data, len);
  host_args[host_arg_count++] = b;
  return TVM_OK;
}

/* 並べた引数の要求を 1 つ処理し、応答を返して引数を空にする。entry から戻ると tcs2c の GC の frame の境界になる */
static TvmStatus f_call(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)user;
  host_out_length = 0;
  tcs_entry_TcsSelf_Sessions_Call();
  for (int i = 0; i < host_arg_count; i++)
    free(host_args[i].data);
  host_arg_count = 0;
  *(void **)args[0].p =
      api->string_new(vm, host_out ? host_out : "", host_out_length);
  rets[0].i = 1;
  return TVM_OK;
}

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a,
                                              TvmVm *vm) {
  static bool started;
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  api = a;
  /* player は VM を作るたびに呼ぶ。tcs の状態 (開いた session) は process に 1 つ */
  if (!started) {
    tcs_lib_init();
    started = true;
  }
  return a->host(vm, "tcs.arg", "s", "", f_arg, NULL) &&
         a->host(vm, "tcs.call", "&s", "b", f_call, NULL);
}
