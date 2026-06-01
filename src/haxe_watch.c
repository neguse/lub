#include "haxe_watch.h"
#include <SDL3/SDL.h>
#include <string.h>

// SDL3 release-3.2.30 の filesystem API を使って `*.hx` を recursive に集める。
//   - SDL_PathInfo (modify_time, type)
//   - SDL_GetPathInfo / SDL_EnumerateDirectory
//   - SDL_PATHTYPE_DIRECTORY / SDL_PATHTYPE_FILE
//   - SDL_ENUM_CONTINUE
//   - SDL_GetTicksNS (debounce)

static Sint64 mtime_ns(const char *path) {
  SDL_PathInfo info;
  if (!SDL_GetPathInfo(path, &info))
    return 0;
  return (Sint64)info.modify_time;
}

static void add_entry(HaxeWatch *w, const char *path) {
  if (w->count == w->cap) {
    int new_cap = w->cap ? w->cap * 2 : 16;
    HaxeWatchEntry *grown = (HaxeWatchEntry *)SDL_realloc(
        w->entries, (size_t)new_cap * sizeof(HaxeWatchEntry));
    if (!grown)
      return;
    w->entries = grown;
    w->cap = new_cap;
  }
  SDL_snprintf(w->entries[w->count].path, sizeof(w->entries[0].path), "%s",
               path);
  w->entries[w->count].mtime_ns = mtime_ns(path);
  w->count++;
}

// SDL_EnumerateDirectory callback. Recurses into subdirectories and adds
// any `*.hx` (case-insensitive) file to the watch list.
static SDL_EnumerationResult SDLCALL enum_cb(void *userdata, const char *dir,
                                             const char *fname) {
  HaxeWatch *w = (HaxeWatch *)userdata;
  if (!w || !dir || !fname)
    return SDL_ENUM_CONTINUE;

  char full[768];
  // `dir` の末尾が `/` を持っているか SDL は保証しない実装があるので、
  // 必ず明示的に separator を入れる。dir が既に `/` で終わっていた場合は
  // `<dir>//<fname>` になっても open には影響しない (POSIX 上は collapse
  // される)。
  SDL_snprintf(full, sizeof(full), "%s/%s", dir, fname);

  SDL_PathInfo info;
  if (!SDL_GetPathInfo(full, &info))
    return SDL_ENUM_CONTINUE;

  if (info.type == SDL_PATHTYPE_DIRECTORY) {
    SDL_EnumerateDirectory(full, enum_cb, w);
  } else if (info.type == SDL_PATHTYPE_FILE) {
    size_t n = SDL_strlen(fname);
    if (n > 3 && SDL_strcasecmp(fname + n - 3, ".hx") == 0) {
      add_entry(w, full);
    }
  }
  return SDL_ENUM_CONTINUE;
}

bool haxe_watch_init(HaxeWatch *w, const char *hxml_path,
                     const HxmlMeta *meta) {
  if (!w)
    return false;
  SDL_zerop(w);
  if (!hxml_path || !meta)
    return false;

  // entry[0] は hxml 自身。haxe_watch_tick の meta_dirty 判定が
  // index 0 を見るのに依存している。
  add_entry(w, hxml_path);

  for (int i = 0; i < meta->cp_count; ++i) {
    const char *cp = meta->cp_paths[i];
    if (!cp[0])
      continue;
    SDL_EnumerateDirectory(cp, enum_cb, w);
  }
  return true;
}

void haxe_watch_shutdown(HaxeWatch *w) {
  if (!w)
    return;
  SDL_free(w->entries);
  SDL_zerop(w);
}

#define DEBOUNCE_NS (50LL * 1000LL * 1000LL) // 50 ms

bool haxe_watch_tick(HaxeWatch *w, bool *meta_dirty) {
  if (meta_dirty)
    *meta_dirty = false;
  if (!w || w->count == 0)
    return false;

  bool any_change = false;
  Sint64 now = (Sint64)SDL_GetTicksNS();
  for (int i = 0; i < w->count; ++i) {
    Sint64 t = mtime_ns(w->entries[i].path);
    if (t != w->entries[i].mtime_ns) {
      // entry[0] は hxml なので、index 0 の変化 = meta_dirty。
      if (i == 0 && meta_dirty)
        *meta_dirty = true;
      w->entries[i].mtime_ns = t;
      any_change = true;
    }
  }
  if (any_change) {
    w->last_change_ns = now;
    w->pending_rebuild = true;
  }
  if (w->pending_rebuild && now - w->last_change_ns >= DEBOUNCE_NS) {
    w->pending_rebuild = false;
    return true;
  }
  return false;
}
