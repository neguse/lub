// Android の player の起動時の準備。
//
// ゲームのデータ (shader cache、音、画像など) は APK の assets に、dev と同じ相対パスで入っている。
// runtime は file を fopen と stat で読むので (hot reload の mtime もある)、assets は file として読めない。
// 起動時に assets を内部ストレージの data/ に写し、そこを cwd にする。
//
// 写す file は assets の lub-files.txt が列挙する。
// 1 行目は APK の中身の印 (build が付ける hash)、2 行目以降が file の相対パス。
// 写し終えた印を data/ の lub-files.txt に書き、印が同じなら次の起動では写さない。
// save/<名前>/ (SDL_GetPrefPath、内部ストレージの直下) とは別の場所なので、写し直しても保存は消えない。
#include "android.h"
#include <SDL3/SDL.h>
#include <errno.h>
#include <string.h>
#include <sys/stat.h>
#include <unistd.h>

#define MANIFEST "lub-files.txt"

static bool make_parents(char *path) {
  for (char *p = path + 1; *p; ++p) {
    if (*p != '/')
      continue;
    *p = '\0';
    bool ok = mkdir(path, 0755) == 0 || errno == EEXIST;
    *p = '/';
    if (!ok) {
      SDL_Log("android: mkdir %s failed: %s", path, strerror(errno));
      return false;
    }
  }
  return true;
}

static bool write_file(char *path, const void *data, size_t size) {
  if (!make_parents(path))
    return false;
  if (!SDL_SaveFile(path, data, size)) {
    SDL_Log("android: writing %s failed: %s", path, SDL_GetError());
    return false;
  }
  return true;
}

// 相対パスは assets の中だけを指すこと (.. や絶対パスは写さない)。
static bool path_ok(const char *path) {
  if (!*path || *path == '/')
    return false;
  for (const char *s = path; *s;) {
    const char *end = strchr(s, '/');
    size_t len = end ? (size_t)(end - s) : strlen(s);
    if (len == 0 || (len == 1 && s[0] == '.') ||
        (len == 2 && s[0] == '.' && s[1] == '.'))
      return false;
    if (!end)
      break;
    s = end + 1;
  }
  return true;
}

static bool copy_assets(const char *dir, char *manifest, size_t size) {
  char *lines = SDL_strndup(manifest, size);
  if (!lines)
    return false;
  bool ok = true;
  int count = 0;
  char *save = NULL;
  // 1 行目は印なので飛ばす
  SDL_strtok_r(lines, "\r\n", &save);
  for (char *line = SDL_strtok_r(NULL, "\r\n", &save); line && ok;
       line = SDL_strtok_r(NULL, "\r\n", &save)) {
    if (!path_ok(line)) {
      SDL_Log("android: bad path in %s: %s", MANIFEST, line);
      ok = false;
      break;
    }
    size_t len = 0;
    // 相対パスは内部ストレージの直下を見てから assets を読む (SDL の Android の SDL_IOFromFile)。
    // 直下にあるのは保存の file だけで、ゲームのデータと同じ名前は置かない。
    void *bytes = SDL_LoadFile(line, &len);
    if (!bytes) {
      SDL_Log("android: reading asset %s failed: %s", line, SDL_GetError());
      ok = false;
      break;
    }
    char *path = NULL;
    SDL_asprintf(&path, "%s/%s", dir, line);
    ok = path && write_file(path, bytes, len);
    SDL_free(path);
    SDL_free(bytes);
    ++count;
  }
  SDL_free(lines);
  if (ok) {
    char *path = NULL;
    SDL_asprintf(&path, "%s/%s", dir, MANIFEST);
    ok = path && write_file(path, manifest, size);
    SDL_free(path);
    SDL_Log("android: copied %d files to %s", count, dir);
  }
  return ok;
}

static bool same_stamp(const char *a, size_t a_len, const char *b,
                       size_t b_len) {
  const char *a_end = memchr(a, '\n', a_len);
  const char *b_end = memchr(b, '\n', b_len);
  size_t n = a_end ? (size_t)(a_end - a) : a_len;
  size_t m = b_end ? (size_t)(b_end - b) : b_len;
  return n > 0 && n == m && memcmp(a, b, n) == 0;
}

bool android_prepare(void) {
  // OpenXR の session は Activity が止まると STOPPING になり、runtime はそれを処理する frame の loop を要る。
  // SDL が pause の間 event の処理で止まると session を終われないので、止めずに回す。
  SDL_SetHint(SDL_HINT_ANDROID_BLOCK_ON_PAUSE, "0");
  const char *internal = SDL_GetAndroidInternalStoragePath();
  if (!internal) {
    SDL_Log("android: no internal storage: %s", SDL_GetError());
    return false;
  }
  char *dir = NULL;
  SDL_asprintf(&dir, "%s/data", internal);
  if (!dir)
    return false;
  bool ok = true;
  size_t size = 0;
  char *manifest = (char *)SDL_LoadFile(MANIFEST, &size);
  if (manifest) {
    char *copied_path = NULL;
    SDL_asprintf(&copied_path, "%s/%s", dir, MANIFEST);
    size_t copied_size = 0;
    char *copied =
        copied_path ? (char *)SDL_LoadFile(copied_path, &copied_size) : NULL;
    if (!copied || !same_stamp(manifest, size, copied, copied_size))
      ok = copy_assets(dir, manifest, size);
    SDL_free(copied);
    SDL_free(copied_path);
    SDL_free(manifest);
  } else {
    // 写す file の無い APK (データを持たないゲーム)
    char *marker = NULL;
    SDL_asprintf(&marker, "%s/.keep", dir);
    ok = marker && make_parents(marker);
    SDL_free(marker);
  }
  if (ok && chdir(dir) != 0) {
    SDL_Log("android: chdir %s failed: %s", dir, strerror(errno));
    ok = false;
  }
  SDL_free(dir);
  return ok;
}
