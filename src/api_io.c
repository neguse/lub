// C API for io / png.
// The runtime owns the file cache (mtime fast path + content-hash version) and
// the loaders. The runtime owns the cache, keyed by path, and returns results
// as frame-scoped views.
#include "api_internal.h"
#include "backend.h"
#include "gltf.h"
#include "stb_image.h"
#include "stb_image_write.h"
#include <SDL3/SDL.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

uint64_t lub_io_fnv1a64(const void *data, size_t len) {
  uint64_t h = 0xcbf29ce484222325ULL;
  const unsigned char *p = (const unsigned char *)data;
  for (size_t i = 0; i < len; ++i) {
    h ^= p[i];
    h *= 0x100000001b3ULL;
  }
  return h;
}

// -------------------------------------------------------- save directory
// save/<name>/<file> refers to the user's save location.
// On native, it is under <name> of SDL_GetPrefPath without an organization name
// (Linux: $XDG_DATA_HOME/<name>/, Windows: %APPDATA%\<name>\). On web, it is
// under /save/<name>/, which is synced to IndexedDB, and it stays Pending until
// it has been read from IndexedDB. When IndexedDB is unavailable, saving to a
// location that does not persist is not allowed, and both loading and saving
// fail with an error. The name and each file path segment must not be empty,
// ".", or ".." (to stay inside the save location).

typedef enum SavePath {
  SAVE_NONE,
  SAVE_OK,
  SAVE_INVALID,
  SAVE_PENDING,
  SAVE_UNAVAILABLE
} SavePath;

#ifdef __EMSCRIPTEN__
#include <emscripten.h>
// clang-format off
EM_JS(void, save_mount_js, (void), {
  Module.lubSave = { state: 0, busy: false, dirty: false };
  try {
    FS.mkdirTree('/save');
    FS.mount(IDBFS, {}, '/save');
  } catch (e) {
    console.error('[lub] save directory:', e);
    Module.lubSave.state = 2;
    return;
  }
  FS.syncfs(true, function (e) {
    if (e) console.error('[lub] save directory:', e);
    Module.lubSave.state = e ? 2 : 1;
  });
});
// 0 is loading, 1 is available, 2 is unavailable.
EM_JS(int, save_state_js, (void), {
  return Module.lubSave.state;
});
// If another save comes in while writing, write again after the current write finishes (syncfs is not run concurrently).
EM_JS(void, save_flush_js, (void), {
  var s = Module.lubSave;
  if (s.busy) {
    s.dirty = true;
    return;
  }
  s.busy = true;
  var run = function () {
    FS.syncfs(false, function (e) {
      if (e) console.error('[lub] save directory:', e);
      if (s.dirty) {
        s.dirty = false;
        run();
      } else {
        s.busy = false;
      }
    });
  };
  run();
});
// clang-format on
#endif

static bool save_segment_ok(const char *s, size_t n) {
  if (n == 0 || (n == 1 && s[0] == '.') ||
      (n == 2 && s[0] == '.' && s[1] == '.'))
    return false;
  for (size_t i = 0; i < n; ++i)
    if (s[i] == '\\' || s[i] == ':')
      return false;
  return true;
}

// true if every '/'-separated segment passes save_segment_ok.
static bool save_segments_ok(const char *s) {
  for (;;) {
    const char *end = strchr(s, '/');
    if (!save_segment_ok(s, end ? (size_t)(end - s) : strlen(s)))
      return false;
    if (!end)
      return true;
    s = end + 1;
  }
}

// If path starts with save/, write the path in the save location to out.
static SavePath save_resolve(const char *path, char *out, size_t cap) {
  if (strncmp(path, "save/", 5) != 0)
    return SAVE_NONE;
  const char *name = path + 5;
  const char *slash = strchr(name, '/');
  if (!slash || !save_segment_ok(name, (size_t)(slash - name)))
    return SAVE_INVALID;
  const char *rest = slash + 1;
  for (const char *seg = rest;;) {
    const char *end = strchr(seg, '/');
    size_t seg_len = end ? (size_t)(end - seg) : strlen(seg);
    if (!save_segment_ok(seg, seg_len))
      return SAVE_INVALID;
    if (!end)
      break;
    seg = end + 1;
  }
#ifdef __EMSCRIPTEN__
  static bool mounted;
  if (!mounted) {
    mounted = true;
    save_mount_js();
  }
  int state = save_state_js();
  if (state == 0)
    return SAVE_PENDING;
  if (state == 2)
    return SAVE_UNAVAILABLE;
  int n = SDL_snprintf(out, cap, "/%s", path);
#else
  size_t name_len = (size_t)(slash - name);
  char app_name[256];
  if (name_len >= sizeof(app_name))
    return SAVE_INVALID;
  memcpy(app_name, name, name_len);
  app_name[name_len] = '\0';
  char *base = SDL_GetPrefPath("", app_name);
  if (!base)
    return SAVE_INVALID;
  int n = SDL_snprintf(out, cap, "%s%s", base, rest);
  SDL_free(base);
#endif
  return n > 0 && (size_t)n < cap ? SAVE_OK : SAVE_INVALID;
}

// ------------------------------------------------------------ file cache

typedef enum IoKind { IO_TEXT = 1, IO_GLTF, IO_PNG } IoKind;

typedef struct IoEntry {
  char *path;
  IoKind kind;
  int64_t mtime;
  uint64_t hash;
  bool parsed_ok; // Whether the parsed contents are valid
  // parsed (per kind)
  uint8_t *bytes; // TEXT: contents (NUL-terminated). Others: unused
  size_t bytes_len;
  GltfMesh *gltf;
  LubGltfPrimitive *gltf_prims; // gltf view (points into the gltf arrays)
  LubGltfMaterial *gltf_mats;
  uint8_t *pixels;
  int32_t w, h, stride;
  struct IoEntry *next;
} IoEntry;

#define IO_BUCKETS 64

struct IoCache {
  IoEntry *buckets[IO_BUCKETS];
};

static void io_entry_free_parsed(IoEntry *e) {
  free(e->bytes);
  e->bytes = NULL;
  e->bytes_len = 0;
  gltf_free(e->gltf);
  e->gltf = NULL;
  free(e->gltf_prims);
  e->gltf_prims = NULL;
  free(e->gltf_mats);
  e->gltf_mats = NULL;
  if (e->pixels)
    stbi_image_free(e->pixels);
  e->pixels = NULL;
  e->parsed_ok = false;
}

static void gltf_free_any(void *m) { gltf_free((GltfMesh *)m); }

// Before replacing the contents on reload, hand the old contents over until the
// end of the frame. Views returned earlier in the same frame (load_bytes /
// png_load / load_gltf) still point to the old contents and stay
// readable after the reload.
static void io_entry_retire_parsed(App *app, IoEntry *e) {
  app_frame_garbage_push(app, e->bytes);
  app_frame_garbage_push_fn(app, e->gltf, gltf_free_any);
  app_frame_garbage_push(app, e->gltf_prims);
  app_frame_garbage_push(app, e->gltf_mats);
  app_frame_garbage_push_fn(app, e->pixels, stbi_image_free);
  e->bytes = NULL;
  e->bytes_len = 0;
  e->gltf = NULL;
  e->gltf_prims = NULL;
  e->gltf_mats = NULL;
  e->pixels = NULL;
  e->parsed_ok = false;
}

void api_io_shutdown(App *app) {
  struct IoCache *c = app->io_cache;
  if (!c)
    return;
  for (int i = 0; i < IO_BUCKETS; ++i) {
    IoEntry *e = c->buckets[i];
    while (e) {
      IoEntry *n = e->next;
      io_entry_free_parsed(e);
      free(e->path);
      free(e);
      e = n;
    }
  }
  free(c);
  app->io_cache = NULL;
}

static IoEntry *io_entry_get(App *app, const char *path, IoKind kind,
                             bool create) {
  if (!app->io_cache) {
    app->io_cache = (struct IoCache *)calloc(1, sizeof(struct IoCache));
    if (!app->io_cache)
      return NULL;
  }
  uint32_t h = (uint32_t)lub_io_fnv1a64(path, strlen(path)) & (IO_BUCKETS - 1);
  for (IoEntry *e = app->io_cache->buckets[h]; e; e = e->next)
    if (e->kind == kind && strcmp(e->path, path) == 0)
      return e;
  if (!create)
    return NULL;
  IoEntry *e = (IoEntry *)calloc(1, sizeof(IoEntry));
  if (!e)
    return NULL;
  e->path = strdup(path);
  e->kind = kind;
  e->next = app->io_cache->buckets[h];
  app->io_cache->buckets[h] = e;
  return e;
}

static uint8_t *read_file(const char *path, size_t *len) {
  FILE *f = fopen(path, "rb");
  if (!f)
    return NULL;
  if (fseek(f, 0, SEEK_END) != 0) {
    fclose(f);
    return NULL;
  }
  long n = ftell(f);
  if (n < 0) {
    fclose(f);
    return NULL;
  }
  rewind(f);
  uint8_t *buf = (uint8_t *)malloc((size_t)n + 1);
  if (!buf) {
    fclose(f);
    return NULL;
  }
  size_t got = fread(buf, 1, (size_t)n, f);
  fclose(f);
  if (got != (size_t)n) {
    free(buf);
    return NULL;
  }
  buf[n] = '\0';
  *len = (size_t)n;
  return buf;
}

// On export, copy the files the game read to the same relative paths in the
// snapshot (the player uses the snapshot as cwd and reads the same paths).
static void export_read(const char *path, const uint8_t *bytes, size_t len) {
#ifdef __EMSCRIPTEN__
  (void)path;
  (void)bytes;
  (void)len;
#else
  export_record(path, bytes, len);
#endif
}

static bool exporting(void) {
#ifdef __EMSCRIPTEN__
  return false;
#else
  return export_active();
#endif
}

static int32_t version_of(uint64_t hash) {
  return (int32_t)(uint32_t)(hash ^ (hash >> 32));
}

// The (version, status, error) tuple of load_*.
typedef struct IoResult {
  int32_t status; // LubIoStatus
  int32_t version;
  LubStr error;
} IoResult;

static void io_result(IoResult *r, int32_t status, int32_t version,
                      const char *error) {
  r->status = status;
  r->version = version;
  r->error = lub_str_c(error);
}

// true if all external gltf buffers (.bin / .glb) exist (copied to the snapshot
// during export). If any is missing, write to r and return false.
static bool gltf_deps_ready(const char *path, const uint8_t *src, size_t len,
                            IoResult *r) {
  const char *base_end = strrchr(path, '/');
  size_t base_len = base_end ? (size_t)(base_end - path + 1) : 0;
  const char *p = (const char *)src;
  const char *end = p + len;
  static char dep_buf[1024];
  while ((p = strstr(p, "\"uri\"")) != NULL && p < end) {
    p += 5;
    while (p < end && (*p == ' ' || *p == ':' || *p == '\t'))
      p++;
    if (p >= end || *p != '"')
      continue;
    p++;
    const char *q = memchr(p, '"', (size_t)(end - p));
    if (!q)
      break;
    size_t ul = (size_t)(q - p);
    bool blocking = (ul > 4 && strncmp(q - 4, ".bin", 4) == 0) ||
                    (ul > 4 && strncmp(q - 4, ".glb", 4) == 0);
    bool requestable = ul > 0 && p[0] != '/' && strncmp(p, "data:", 5) != 0 &&
                       strncmp(p, "http:", 5) != 0 &&
                       strncmp(p, "https:", 6) != 0;
    if (blocking && requestable && base_len + ul < sizeof(dep_buf)) {
      memcpy(dep_buf, path, base_len);
      memcpy(dep_buf + base_len, p, ul);
      dep_buf[base_len + ul] = '\0';
      if (app_file_mtime_ns(dep_buf) == 0) {
        io_result(r, LUB_IO_STATUS_ERROR, 0, dep_buf);
        return false;
      }
      if (exporting()) {
        size_t dep_len = 0;
        uint8_t *dep = read_file(dep_buf, &dep_len);
        if (dep)
          export_read(dep_buf, dep, dep_len);
        free(dep);
      }
    }
    p = q + 1;
  }
  return true;
}

static void gltf_build_view(IoEntry *e) {
  GltfMesh *m = e->gltf;
  e->gltf_prims = (LubGltfPrimitive *)calloc(
      m->primitive_count ? m->primitive_count : 1, sizeof(LubGltfPrimitive));
  e->gltf_mats = (LubGltfMaterial *)calloc(
      m->material_count ? m->material_count : 1, sizeof(LubGltfMaterial));
  if (!e->gltf_prims || !e->gltf_mats)
    return;
  for (int i = 0; i < m->material_count; ++i) {
    const GltfMaterial *g = &m->materials[i];
    LubGltfMaterial *v = &e->gltf_mats[i];
    memcpy(v->base_color_factor, g->base_color_factor,
           sizeof(v->base_color_factor));
    v->base_color_factor_count = 4;
    v->metallic_factor = g->metallic_factor;
    v->roughness_factor = g->roughness_factor;
    v->alpha_mode = g->alpha_mode;
    v->alpha_cutoff = g->alpha_cutoff;
    v->double_sided = g->double_sided;
    v->normal_scale = g->normal_scale;
    v->base_color_path = lub_str_c(g->base_color_path);
    v->metallic_roughness_path = lub_str_c(g->metallic_roughness_path);
    v->normal_path = lub_str_c(g->normal_path);
    v->name = lub_str_c(g->name);
  }
  for (int i = 0; i < m->primitive_count; ++i) {
    const GltfPrimitive *p = &m->primitives[i];
    LubGltfPrimitive *v = &e->gltf_prims[i];
    v->mesh.positions = p->positions;
    v->mesh.positions_count = p->vert_count * 3;
    v->mesh.normals = p->normals;
    v->mesh.normals_count = p->normals ? p->vert_count * 3 : 0;
    v->mesh.uvs = p->uvs;
    v->mesh.uvs_count = p->uvs ? p->vert_count * 2 : 0;
    v->mesh.tangents = p->tangents;
    v->mesh.tangents_count = p->tangents ? p->vert_count * 4 : 0;
    v->mesh.indices = (const int32_t *)p->indices;
    v->mesh.indices_count = p->indices ? p->index_count : 0;
    v->mesh.vert_count = p->vert_count;
    v->mesh.index_count = p->index_count;
    v->material_index = p->material_index;
    if (p->material_index >= 0 && p->material_index < m->material_count) {
      v->has_material = true;
      v->material = e->gltf_mats[p->material_index];
    }
  }
}

// refresh shared by all loaders.
// Returns the entry (its contents are usable if parsed_ok) or NULL.
// r receives status / version / error.
static IoEntry *io_refresh(App *app, LubStr path_s, IoKind kind, IoResult *r) {
  char path[1024];
  if (!lub_str_copy(path_s, path, sizeof(path))) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "path too long");
    return NULL;
  }
  char save_file[1024];
  SavePath save = save_resolve(path, save_file, sizeof(save_file));
  if (save == SAVE_INVALID) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "invalid save path");
    return NULL;
  }
  if (save == SAVE_PENDING) {
    io_result(r, LUB_IO_STATUS_PENDING, 0, NULL);
    return NULL;
  }
  if (save == SAVE_UNAVAILABLE) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "the save directory is not available");
    return NULL;
  }
  const char *file = save == SAVE_OK ? save_file : path;
  IoEntry *e = io_entry_get(app, path, kind, true);
  if (!e) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "out of memory");
    return NULL;
  }
  int64_t mtime = app_file_mtime_ns(file);
  if (mtime == 0) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "missing");
    return NULL;
  }
  if (e->parsed_ok && e->mtime == mtime) {
    io_result(r, LUB_IO_STATUS_READY, version_of(e->hash), NULL);
    return e;
  }
  size_t len = 0;
  uint8_t *bytes = read_file(file, &len);
  if (!bytes) {
    io_result(r, LUB_IO_STATUS_ERROR, e->parsed_ok ? version_of(e->hash) : 0,
              "missing");
    return e->parsed_ok ? e : NULL;
  }
  // Do not put user saves in the export snapshot
  if (save == SAVE_NONE)
    export_read(path, bytes, len);
  uint64_t hash = lub_io_fnv1a64(bytes, len);
  if (e->parsed_ok && e->hash == hash) {
    e->mtime = mtime; // Contents unchanged, update only mtime
    free(bytes);
    io_result(r, LUB_IO_STATUS_READY, version_of(hash), NULL);
    return e;
  }

  static char err[512];
  err[0] = '\0';
  switch (kind) {
  case IO_TEXT: {
    io_entry_retire_parsed(app, e);
    e->bytes = bytes;
    e->bytes_len = len;
    bytes = NULL;
    e->parsed_ok = true;
    break;
  }
  case IO_GLTF: {
    size_t pl = strlen(path);
    bool is_json = pl > 5 && strcmp(path + pl - 5, ".gltf") == 0;
    // External buffers under save/ are not exported, so skip the pre-check;
    // gltf_load checks that they do not leave the save location and reads them
    if (save == SAVE_NONE && is_json && !gltf_deps_ready(path, bytes, len, r)) {
      free(bytes);
      return e->parsed_ok ? e : NULL;
    }
    GltfMesh *m =
        gltf_load(file, path, save == SAVE_NONE ? NULL : save_segments_ok, err,
                  sizeof(err));
    if (!m) {
      SDL_Log("%s", err);
      break;
    }
    io_entry_retire_parsed(app, e);
    e->gltf = m;
    gltf_build_view(e);
    e->parsed_ok = e->gltf_prims && e->gltf_mats;
    break;
  }
  case IO_PNG: {
    int w = 0, h = 0;
    unsigned char *px =
        stbi_load_from_memory(bytes, (int)len, &w, &h, NULL, STBI_rgb_alpha);
    if (!px) {
      snprintf(err, sizeof(err), "png_load failed: %s", stbi_failure_reason());
      SDL_Log("png_load: %s: %s", path, stbi_failure_reason());
      break;
    }
    io_entry_retire_parsed(app, e);
    e->pixels = px;
    e->w = w;
    e->h = h;
    e->stride = w * STBI_rgb_alpha;
    e->parsed_ok = true;
    break;
  }
  }
  free(bytes);
  if (err[0]) {
    // parse failure: keep the previous value without updating the cache
    io_result(r, LUB_IO_STATUS_ERROR, e->parsed_ok ? version_of(e->hash) : 0,
              err);
    return e->parsed_ok ? e : NULL;
  }
  e->mtime = mtime;
  e->hash = hash;
  io_result(r, LUB_IO_STATUS_READY, version_of(hash), NULL);
  return e;
}

static void io_tail(const IoResult *r, int32_t *version, int32_t *status,
                    LubStr *error) {
  *version = r->version;
  *status = r->status;
  *error = r->error;
}

LubStatus lub_io_load_text(LubContext *ctx, LubStr path, LubStr *text,
                           int32_t *version, int32_t *status, LubStr *error) {
  App *app = lub_api_app(ctx);
  IoResult r;
  text->ptr = NULL;
  text->len = 0;
  IoEntry *e = io_refresh(app, path, IO_TEXT, &r);
  if (e) {
    text->ptr = (const char *)e->bytes;
    text->len = (int32_t)e->bytes_len;
  }
  io_tail(&r, version, status, error);
  return LUB_OK;
}

LubStatus lub_io_save_text(LubContext *ctx, LubStr path, LubStr text) {
  App *app = lub_api_app(ctx);
  char key[1024], file[1024], temporary[1088], parent[1024];
  if (!path.ptr || path.len <= 0 || memchr(path.ptr, 0, (size_t)path.len) ||
      !lub_str_copy(path, key, sizeof(key)))
    return lub_api_fail(app, "io.save_text: invalid path");
  if (text.len < 0 || (text.len > 0 && !text.ptr))
    return lub_api_fail(app, "io.save_text: invalid text");
  SavePath save = save_resolve(key, file, sizeof(file));
  if (save == SAVE_INVALID)
    return lub_api_fail(app,
                        "io.save_text: invalid save path (save/<name>/<file>)");
  if (save == SAVE_PENDING)
    return lub_api_fail(app,
                        "io.save_text: the save directory is still loading");
  if (save == SAVE_UNAVAILABLE)
    return lub_api_fail(app,
                        "io.save_text: the save directory is not available");
  if (save == SAVE_NONE)
    SDL_strlcpy(file, key, sizeof(file));
  SDL_strlcpy(parent, file, sizeof(parent));
  char *separator = strrchr(parent, '/');
#ifdef _WIN32
  char *backslash = strrchr(parent, '\\');
  if (backslash && (!separator || backslash > separator))
    separator = backslash;
#endif
  if (separator && separator != parent) {
    *separator = 0;
    if (!SDL_CreateDirectory(parent))
      return lub_api_fail(app, "io.save_text: %s", SDL_GetError());
  }
  SDL_snprintf(temporary, sizeof(temporary), "%s.lub-%" SDL_PRIu64 ".tmp", file,
               SDL_GetPerformanceCounter());
  SDL_IOStream *stream = SDL_IOFromFile(temporary, "wbx");
  if (!stream)
    return lub_api_fail(app, "io.save_text: %s", SDL_GetError());
  if (!SDL_SaveFile_IO(stream, text.ptr ? text.ptr : "", (size_t)text.len,
                       true) ||
      !SDL_RenamePath(temporary, file)) {
    LubStatus result = lub_api_fail(app, "io.save_text: %s", SDL_GetError());
    SDL_RemovePath(temporary);
    return result;
  }
  if (app->io_cache)
    for (int i = 0; i < IO_BUCKETS; ++i)
      for (IoEntry *e = app->io_cache->buckets[i]; e; e = e->next)
        if (strcmp(e->path, key) == 0)
          e->mtime = 0;
#ifdef __EMSCRIPTEN__
  if (save == SAVE_OK)
    save_flush_js();
#endif
  return LUB_OK;
}

LubStatus lub_io_load_bytes(LubContext *ctx, LubStr path, LubView *bytes,
                            int32_t *version, int32_t *status, LubStr *error) {
  App *app = lub_api_app(ctx);
  IoResult r;
  memset(bytes, 0, sizeof(*bytes));
  IoEntry *e = io_refresh(app, path, IO_TEXT, &r);
  if (e) {
    bytes->ptr = e->bytes;
    bytes->len = (int32_t)e->bytes_len;
    bytes->frame = (int32_t)app->frame_index;
  }
  io_tail(&r, version, status, error);
  return LUB_OK;
}

// The top level is a copy of primitives[0] + primitives.
LubStatus lub_io_load_gltf(LubContext *ctx, LubStr path, LubGltfMesh *mesh,
                           bool *has_mesh, int32_t *version, int32_t *status,
                           LubStr *error) {
  App *app = lub_api_app(ctx);
  IoResult r;
  memset(mesh, 0, sizeof(*mesh));
  *has_mesh = false;
  IoEntry *e = io_refresh(app, path, IO_GLTF, &r);
  if (e && e->gltf_prims && e->gltf->primitive_count > 0) {
    mesh->primitives = e->gltf_prims;
    mesh->primitives_count = e->gltf->primitive_count;
    mesh->mesh = e->gltf_prims[0].mesh;
    mesh->has_material = e->gltf_prims[0].has_material;
    mesh->material = e->gltf_prims[0].material;
    *has_mesh = true;
  }
  io_tail(&r, version, status, error);
  return LUB_OK;
}

LubStatus lub_png_load(LubContext *ctx, LubStr path, LubView *bytes,
                       int32_t *width, int32_t *height, int32_t *format,
                       int32_t *stride, int32_t *version, int32_t *status,
                       LubStr *error) {
  App *app = lub_api_app(ctx);
  IoResult r;
  memset(bytes, 0, sizeof(*bytes));
  *width = 0;
  *height = 0;
  *format = 0;
  *stride = 0;
  IoEntry *e = io_refresh(app, path, IO_PNG, &r);
  if (e) {
    bytes->ptr = e->pixels;
    bytes->len = e->stride * e->h;
    bytes->frame = (int32_t)app->frame_index;
    *width = e->w;
    *height = e->h;
    *format = LUB_GFX_PIXEL_FORMAT_RGBA8;
    *stride = e->stride;
  }
  io_tail(&r, version, status, error);
  return LUB_OK;
}

LubStatus lub_png_write(LubContext *ctx, LubStr path, const uint8_t *pixels,
                        int32_t len, int32_t w, int32_t h,
                        const int32_t *stride_p) {
  App *app = lub_api_app(ctx);
  int32_t stride = stride_p ? *stride_p : 0;
  char pbuf[1024];
  if (!lub_str_copy(path, pbuf, sizeof(pbuf)))
    return lub_api_fail(app, "png_write: path too long");
  if (w <= 0 || h <= 0)
    return lub_api_fail(app, "png_write: invalid size %dx%d", w, h);
  if (stride <= 0)
    stride = w * 4;
  if (stride < w * 4)
    return lub_api_fail(app, "png_write: stride %d is smaller than width*4 %d",
                        stride, w * 4);
  if (!pixels || (int64_t)len < (int64_t)stride * h)
    return lub_api_fail(app,
                        "png_write: byte buffer too small: got %d, need %d",
                        len, stride * h);
  if (!stbi_write_png(pbuf, w, h, 4, pixels, stride))
    return lub_api_fail(app, "png_write: write failed: %s", pbuf);
  return LUB_OK;
}

// ------------------------------------------------------------ interleave

enum {
  LUB_MESH_LAYOUT_PN = 1,
  LUB_MESH_LAYOUT_PNU = 2,
  LUB_MESH_LAYOUT_PNUT = 3,
  LUB_MESH_LAYOUT_PNCM = 4,
  LUB_MESH_LAYOUT_PNCMW = 5,
};

// Floats per vertex. Every float3 is followed by one pad float and every
// struct is a multiple of 16 bytes, so the same bytes match the shader-side
// StructuredBuffer struct on every target (shader.cpp enforces the rule):
//   pn    float3 pos; float pad; float3 nrm; float pad;
//   pnu   pn + float2 uv; float2 pad;
//   pnut  pnu + float4 tangent;
//   pncm  pn + float3 albedo; float pad; float2 mr; float2 pad;
//   pncmw pncm + float4 skin (j0, w0, j1, w1);
static int32_t layout_stride(int32_t layout) {
  switch (layout) {
  case LUB_MESH_LAYOUT_PN:
    return 8;
  case LUB_MESH_LAYOUT_PNU:
    return 12;
  case LUB_MESH_LAYOUT_PNUT:
    return 16;
  case LUB_MESH_LAYOUT_PNCM:
    return 16;
  case LUB_MESH_LAYOUT_PNCMW:
    return 20;
  default:
    return 0;
  }
}

static int32_t interleave(const LubMeshData *mesh, int32_t layout, float *out,
                          int32_t cap) {
  int32_t stride = layout_stride(layout);
  if (!mesh || stride == 0 || mesh->vert_count < 0 || !mesh->positions)
    return 0;
  int32_t n = mesh->vert_count;
  if (mesh->positions_count < n * 3)
    return 0;
  int32_t need = n * stride;
  if (!out || cap < need)
    return need;
  // Optional arrays are used only when they have enough elements (short / empty
  // arrays are treated as missing)
  bool has_normals = mesh->normals && mesh->normals_count >= n * 3;
  bool has_uvs = mesh->uvs && mesh->uvs_count >= n * 2;
  bool has_tangents = mesh->tangents && mesh->tangents_count >= n * 4;
  bool has_colors = mesh->colors && mesh->colors_count >= n * 3;
  bool has_mr = mesh->metal_rough && mesh->metal_rough_count >= n * 2;
  bool has_skin = mesh->joints && mesh->weights &&
                  mesh->joints_count >= n * 2 && mesh->weights_count >= n * 2;
  float *o = out;
  for (int32_t i = 0; i < n; ++i) {
    const float *p = mesh->positions + i * 3;
    *o++ = p[0];
    *o++ = p[1];
    *o++ = p[2];
    *o++ = 0; // pad
    if (has_normals) {
      const float *nm = mesh->normals + i * 3;
      *o++ = nm[0];
      *o++ = nm[1];
      *o++ = nm[2];
    } else {
      *o++ = 0;
      *o++ = 0;
      *o++ = 1;
    }
    *o++ = 0; // pad
    if (layout == LUB_MESH_LAYOUT_PNU || layout == LUB_MESH_LAYOUT_PNUT) {
      if (has_uvs) {
        *o++ = mesh->uvs[i * 2];
        *o++ = mesh->uvs[i * 2 + 1];
      } else {
        *o++ = 0;
        *o++ = 0;
      }
      *o++ = 0; // pad
      *o++ = 0;
    }
    if (layout == LUB_MESH_LAYOUT_PNUT) {
      // When tangents are missing, set w=0 so the shader falls back to
      // derivative TBN
      if (has_tangents) {
        const float *t = mesh->tangents + i * 4;
        *o++ = t[0];
        *o++ = t[1];
        *o++ = t[2];
        *o++ = t[3];
      } else {
        *o++ = 1;
        *o++ = 0;
        *o++ = 0;
        *o++ = 0;
      }
    }
    if (layout == LUB_MESH_LAYOUT_PNCM || layout == LUB_MESH_LAYOUT_PNCMW) {
      // Missing colors become 0.8 gray, missing metal_rough becomes (0, 0.8)
      if (has_colors) {
        const float *c = mesh->colors + i * 3;
        *o++ = c[0];
        *o++ = c[1];
        *o++ = c[2];
      } else {
        *o++ = 0.8f;
        *o++ = 0.8f;
        *o++ = 0.8f;
      }
      *o++ = 0; // pad
      if (has_mr) {
        *o++ = mesh->metal_rough[i * 2];
        *o++ = mesh->metal_rough[i * 2 + 1];
      } else {
        *o++ = 0;
        *o++ = 0.8f;
      }
      *o++ = 0; // pad
      *o++ = 0;
    }
    if (layout == LUB_MESH_LAYOUT_PNCMW) {
      // Missing joints/weights become "weight 1 on bone 0"
      if (has_skin) {
        *o++ = mesh->joints[i * 2];
        *o++ = mesh->weights[i * 2];
        *o++ = mesh->joints[i * 2 + 1];
        *o++ = mesh->weights[i * 2 + 1];
      } else {
        *o++ = 0;
        *o++ = 1;
        *o++ = 0;
        *o++ = 0;
      }
    }
  }
  return need;
}

// The interleave result is a runtime-owned array valid until the end of the
// frame.
static LubStatus interleave_view(LubContext *ctx, const LubMeshData *mesh,
                                 int32_t layout, const char *fn,
                                 const float **out, int32_t *out_count) {
  App *app = lub_api_app(ctx);
  *out = NULL;
  *out_count = 0;
  if (!mesh || !mesh->positions || mesh->vert_count <= 0 ||
      mesh->positions_count < mesh->vert_count * 3)
    return lub_api_fail(app, "%s: mesh has no positions", fn);
  int32_t need = interleave(mesh, layout, NULL, 0);
  float *buf = (float *)malloc(sizeof(float) * (size_t)(need > 0 ? need : 1));
  if (!buf)
    return lub_api_fail(app, "%s: out of memory", fn);
  interleave(mesh, layout, buf, need);
  app_frame_garbage_push(app, buf);
  *out = buf;
  *out_count = need;
  return LUB_OK;
}

LubStatus lub_io_interleave_pn(LubContext *ctx, const LubMeshData *mesh,
                               const float **out, int32_t *out_count) {
  return interleave_view(ctx, mesh, LUB_MESH_LAYOUT_PN, "interleave_pn", out,
                         out_count);
}

LubStatus lub_io_interleave_pncm(LubContext *ctx, const LubMeshData *mesh,
                                 const float **out, int32_t *out_count) {
  return interleave_view(ctx, mesh, LUB_MESH_LAYOUT_PNCM, "interleave_pncm",
                         out, out_count);
}

LubStatus lub_io_interleave_pncmw(LubContext *ctx, const LubMeshData *mesh,
                                  const float **out, int32_t *out_count) {
  return interleave_view(ctx, mesh, LUB_MESH_LAYOUT_PNCMW, "interleave_pncmw",
                         out, out_count);
}

LubStatus lub_io_interleave_pnu(LubContext *ctx, const LubMeshData *mesh,
                                const float **out, int32_t *out_count) {
  return interleave_view(ctx, mesh, LUB_MESH_LAYOUT_PNU, "interleave_pnu", out,
                         out_count);
}

LubStatus lub_io_interleave_pnut(LubContext *ctx, const LubMeshData *mesh,
                                 const float **out, int32_t *out_count) {
  return interleave_view(ctx, mesh, LUB_MESH_LAYOUT_PNUT, "interleave_pnut",
                         out, out_count);
}
