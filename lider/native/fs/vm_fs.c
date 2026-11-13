/* file system の操作 (src/Fs.ref.cs の関数) を、tcs VM の player の native の拡張として出す。
 * lider の player (native/CMakeLists.txt) は静的に link する (入口の名前は lub_vm_extension_fs)。
 * lub の既製の lub_vm は、scripts/build-native.sh が作る src/Fs.so を読む。
 * web (emscripten) は仮想ファイル (MEMFS) が相手。 */
#include <lub/lub_vm_extension.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <sys/stat.h>
#ifdef _WIN32
#include <direct.h>
#include <io.h>
#include <windows.h>
#else
#include <dirent.h>
#include <limits.h>
#include <unistd.h>
#endif

static const LubVmExtensionApi *api;

/* VM の string を NUL で終わる文字列に写す (呼ぶ側が free) */
static char *dup_arg(const void *s) {
  size_t len = 0;
  const char *data = s ? api->string_data(s, &len) : NULL;
  if (!data)
    return NULL;
  char *out = (char *)malloc(len + 1);
  if (out) {
    memcpy(out, data, len);
    out[len] = '\0';
  }
  return out;
}

static TvmStatus f_stat(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)user;
  char *path = dup_arg(args[0].p);
  struct stat st;
  bool ok = path && stat(path, &st) == 0;
  free(path);
  rets[0].i = ok;
  *(bool *)args[1].p = ok && (st.st_mode & S_IFMT) == S_IFDIR;
  *(int32_t *)args[2].p =
      ok ? (st.st_size > 0x7fffffff ? 0x7fffffff : (int32_t)st.st_size) : 0;
  *(int32_t *)args[3].p = ok ? (int32_t)st.st_mtime : 0;
  return TVM_OK;
}

static int cmp_name(const void *a, const void *b) {
  return strcmp(*(char *const *)a, *(char *const *)b);
}

typedef struct Names {
  char **v;
  size_t n, cap;
} Names;

static bool add_name(Names *names, const char *name, bool dir) {
  if (names->n == names->cap) {
    size_t cap = names->cap ? names->cap * 2 : 32;
    char **g = (char **)realloc(names->v, cap * sizeof(char *));
    if (!g)
      return false;
    names->v = g;
    names->cap = cap;
  }
  size_t len = strlen(name);
  char *copy = (char *)malloc(len + 2);
  if (!copy)
    return false;
  memcpy(copy, name, len);
  copy[len] = dir ? '/' : '\0';
  copy[len + dir] = '\0';
  names->v[names->n++] = copy;
  return true;
}

static bool list_names(const char *path, Names *names) {
#ifdef _WIN32
  char pattern[MAX_PATH + 3];
  snprintf(pattern, sizeof pattern, "%s\\*", path);
  WIN32_FIND_DATAA fd;
  HANDLE h = FindFirstFileA(pattern, &fd);
  if (h == INVALID_HANDLE_VALUE)
    return false;
  do {
    if (strcmp(fd.cFileName, ".") && strcmp(fd.cFileName, ".."))
      add_name(names, fd.cFileName,
               (fd.dwFileAttributes & FILE_ATTRIBUTE_DIRECTORY) != 0);
  } while (FindNextFileA(h, &fd));
  FindClose(h);
  return true;
#else
  DIR *d = opendir(path);
  if (!d)
    return false;
  struct dirent *e;
  while ((e = readdir(d)) != NULL) {
    if (!strcmp(e->d_name, ".") || !strcmp(e->d_name, ".."))
      continue;
    size_t len = strlen(path) + strlen(e->d_name) + 2;
    char *full = (char *)malloc(len);
    struct stat st;
    bool dir = false;
    if (full) {
      snprintf(full, len, "%s/%s", path, e->d_name);
      dir = stat(full, &st) == 0 && (st.st_mode & S_IFMT) == S_IFDIR;
      free(full);
    }
    add_name(names, e->d_name, dir);
  }
  closedir(d);
  return true;
#endif
}

static TvmStatus f_list_dir(TvmVm *vm, TvmSlot *args, TvmSlot *rets,
                            void *user) {
  (void)user;
  char *path = dup_arg(args[0].p);
  Names names = {0};
  bool ok = path && list_names(path, &names);
  free(path);
  rets[0].i = ok;
  *(void **)args[1].p = NULL;
  if (ok) {
    qsort(names.v, names.n, sizeof(char *), cmp_name);
    size_t total = 0;
    for (size_t i = 0; i < names.n; i++)
      total += strlen(names.v[i]) + 1;
    char *text = (char *)malloc(total + 1);
    size_t at = 0;
    for (size_t i = 0; text && i < names.n; i++) {
      size_t len = strlen(names.v[i]);
      memcpy(text + at, names.v[i], len);
      at += len;
      text[at++] = '\n';
    }
    *(void **)args[1].p = api->string_new(vm, text ? text : "", text ? at : 0);
    free(text);
  }
  for (size_t i = 0; i < names.n; i++)
    free(names.v[i]);
  free(names.v);
  return TVM_OK;
}

static TvmStatus f_absolute_path(TvmVm *vm, TvmSlot *args, TvmSlot *rets,
                                 void *user) {
  (void)user;
  char *path = dup_arg(args[0].p);
#ifdef _WIN32
  char buf[MAX_PATH];
  bool ok = path && _fullpath(buf, path, sizeof buf) && _access(buf, 0) == 0;
#else
  char buf[PATH_MAX];
  bool ok = path && realpath(path, buf) != NULL;
#endif
  free(path);
  rets[0].i = ok;
  *(void **)args[1].p = ok ? api->string_new(vm, buf, strlen(buf)) : NULL;
  return TVM_OK;
}

static TvmStatus f_remove(TvmVm *vm, TvmSlot *args, TvmSlot *rets, void *user) {
  (void)vm;
  (void)user;
  char *path = dup_arg(args[0].p);
  struct stat st;
  bool ok = false;
  if (path && stat(path, &st) == 0)
#ifdef _WIN32
    ok = (st.st_mode & S_IFMT) == S_IFDIR ? _rmdir(path) == 0
                                          : remove(path) == 0;
#else
    ok =
        (st.st_mode & S_IFMT) == S_IFDIR ? rmdir(path) == 0 : unlink(path) == 0;
#endif
  free(path);
  rets[0].i = ok;
  return TVM_OK;
}

LUB_VM_EXTENSION_EXPORT bool lub_vm_extension(const LubVmExtensionApi *a,
                                              TvmVm *vm) {
  if (a->version != LUB_VM_EXTENSION_VERSION)
    return false;
  api = a;
  return a->host(vm, "fs.stat", "s&b&i&i", "b", f_stat, NULL) &&
         a->host(vm, "fs.list_dir", "s&s", "b", f_list_dir, NULL) &&
         a->host(vm, "fs.absolute_path", "s&s", "b", f_absolute_path, NULL) &&
         a->host(vm, "fs.remove", "s", "b", f_remove, NULL);
}
