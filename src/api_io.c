// io / png の C API。samples/lub_io.lua と samples/lubx_png.lua にあった
// file cache (mtime の fast path + 内容 hash の version) と loader を runtime
// に移したもの。cache は path を key に runtime が所有し、結果は frame 有効の
// view で返す。
#include "api_internal.h"
#include "gltf.h"
#include "stb_image.h"
#include "stb_image_write.h"
#include <SDL3/SDL.h>
#ifdef __EMSCRIPTEN__
#include <emscripten/emscripten.h>
#endif
#include <ctype.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>

#ifdef __EMSCRIPTEN__
enum {
  LUB_FILE_PENDING = 0,
  LUB_FILE_READY = 1,
  LUB_FILE_ERROR = 2,
};

// EM_JS bodies are JavaScript (see shader.cpp's bridge note).
// clang-format off
EM_JS(int, lub_web_request_file_js, (const char *c_path), {
  var raw = UTF8ToString(c_path);
  if (!raw || raw.length == 0)
    return 2;

  var path = raw.charAt(0) == "/" ? raw.substring(1) : raw;
  var fs = Module["FS"];
  if (!fs && typeof FS != "undefined")
    fs = FS;
  if (!fs)
    return 2;

  try {
    fs.stat(path);
    return 1;
  }
  catch(e) {}

  var requests = Module["__lubFileRequests"];
  if (!requests)
    requests = Module["__lubFileRequests"] = Object.create(null);

  var req = requests[path];
  if (req)
    return req.status;

  req = requests[path] = {status : 0};
  fetch("/" + path)
      .then(function(response) {
        if (!response.ok)
          throw new Error(response.status + " " + response.statusText);
        return response.arrayBuffer();
      })
      .then(function(buffer) {
        var parts = path.split("/");
        var cur = "";
        for (var i = 0; i < parts.length - 1; ++i) {
          if (!parts[i])
            continue;
          cur = cur ? cur + "/" + parts[i] : parts[i];
          try { fs.mkdir(cur); }
          catch(e) {}
        }
        try { fs.unlink(path); }
        catch(e) {}
        fs.writeFile(path, new Uint8Array(buffer));
        req.status = 1;
      })
      .catch(function(e) {
        req.status = 2;
        req.error = String((e && e.message) || e);
        if (typeof console != "undefined" && console.error)
          console.error("[lub] request_file failed:", path, req.error);
      });
  return 0;
})
// clang-format on
#endif

// request_file(path): web は fetch を積んで MEMFS に置く (pending / ready /
// error)、native は存在確認だけ。
int lub_io_request_file(const char *path) {
#ifdef __EMSCRIPTEN__
  return lub_web_request_file_js(path);
#else
  return app_file_mtime_ns(path) != 0 ? 1 : 2;
#endif
}

uint64_t lub_io_fnv1a64(const void *data, size_t len) {
  uint64_t h = 0xcbf29ce484222325ULL;
  const unsigned char *p = (const unsigned char *)data;
  for (size_t i = 0; i < len; ++i) {
    h ^= p[i];
    h *= 0x100000001b3ULL;
  }
  return h;
}

// ------------------------------------------------------------ file cache

typedef enum IoKind { IO_TEXT = 1, IO_FLOATS, IO_GLTF, IO_PNG } IoKind;

typedef struct IoEntry {
  char *path;
  IoKind kind;
  int64_t mtime;
  uint64_t hash;
  bool parsed_ok; // parsed の中身が有効か
  // parsed (kind ごと)
  uint8_t *bytes; // TEXT: 内容 (NUL 終端付き)。他: 未使用
  size_t bytes_len;
  float *floats;
  int32_t float_count;
  GltfMesh *gltf;
  LubGltfPrimitive *gltf_prims; // gltf の view (gltf の配列を指す)
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
  free(e->floats);
  e->floats = NULL;
  e->float_count = 0;
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

static int32_t version_of(uint64_t hash) {
  return (int32_t)(uint32_t)(hash ^ (hash >> 32));
}

static void io_result(LubIoResult *r, int32_t status, int32_t version,
                      const char *error) {
  r->status = status;
  r->version = version;
  r->error = lub_str_c(error);
}

// request_file の結果を LubIoResult に写す (missing / pending)。
static void io_result_request(App *app, LubIoResult *r, const char *path,
                              int32_t keep_version) {
  int st = lub_io_request_file(path);
  if (st == 0) {
    io_result(r, LUB_IO_STATUS_PENDING, keep_version, NULL);
  } else {
    (void)app;
    io_result(r, LUB_IO_STATUS_ERROR, keep_version, "missing");
  }
}

// `return { n, n, ... }` 形式の Lua ファイルを float 列として読む。数値と
// `,` `{` `}` と `--` コメント以外は受け付けない。
static bool parse_floats(const char *src, size_t len, float **out,
                         int32_t *out_count, char *err, size_t err_size) {
  const char *p = src;
  const char *end = src + len;
  float *buf = NULL;
  int32_t n = 0, cap = 0;
  int depth = 0;
  bool saw_return = false;
  while (p < end) {
    if (isspace((unsigned char)*p)) {
      p++;
      continue;
    }
    if (p + 1 < end && p[0] == '-' && p[1] == '-') {
      while (p < end && *p != '\n')
        p++;
      continue;
    }
    if (!saw_return) {
      if (end - p >= 6 && strncmp(p, "return", 6) == 0) {
        p += 6;
        saw_return = true;
        continue;
      }
      snprintf(err, err_size, "expected 'return'");
      free(buf);
      return false;
    }
    if (*p == '{') {
      depth++;
      p++;
      continue;
    }
    if (*p == '}') {
      depth--;
      p++;
      continue;
    }
    if (*p == ',' || *p == ';') {
      p++;
      continue;
    }
    if (depth != 1) {
      snprintf(err, err_size, "unexpected '%c'", *p);
      free(buf);
      return false;
    }
    char *num_end = NULL;
    double v = strtod(p, &num_end);
    if (num_end == p) {
      snprintf(err, err_size, "unexpected '%c' (numbers only)", *p);
      free(buf);
      return false;
    }
    if (n >= cap) {
      cap = cap ? cap * 2 : 256;
      float *grown = (float *)realloc(buf, (size_t)cap * sizeof(float));
      if (!grown) {
        free(buf);
        snprintf(err, err_size, "out of memory");
        return false;
      }
      buf = grown;
    }
    buf[n++] = (float)v;
    p = num_end;
  }
  if (!saw_return || depth != 0) {
    snprintf(err, err_size, "unbalanced table");
    free(buf);
    return false;
  }
  *out = buf;
  *out_count = n;
  return true;
}

// gltf の外部 buffer (.bin / .glb) を web で先に request する。全部 ready
// なら true。pending / error は r に書いて false。
static bool gltf_deps_ready(App *app, const char *path, const uint8_t *src,
                            size_t len, LubIoResult *r) {
  (void)app;
  const char *base_end = strrchr(path, '/');
  size_t base_len = base_end ? (size_t)(base_end - path + 1) : 0;
  const char *p = (const char *)src;
  const char *end = p + len;
  const char *pending_dep = NULL;
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
      int st = lub_io_request_file(dep_buf);
      if (st == 2) {
        io_result(r, LUB_IO_STATUS_ERROR, 0, dep_buf);
        return false;
      }
      if (st == 0 && !pending_dep)
        pending_dep = dep_buf;
    }
    p = q + 1;
  }
  if (pending_dep) {
    io_result(r, LUB_IO_STATUS_PENDING, 0, NULL);
    return false;
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
  for (int i = 0; i < m->primitive_count; ++i) {
    const GltfPrimitive *p = &m->primitives[i];
    LubGltfPrimitive *v = &e->gltf_prims[i];
    v->mesh.positions = p->positions;
    v->mesh.normals = p->normals;
    v->mesh.uvs = p->uvs;
    v->mesh.tangents = p->tangents;
    v->mesh.indices = p->indices;
    v->mesh.vert_count = p->vert_count;
    v->mesh.index_count = p->index_count;
    v->material_index = p->material_index;
  }
  for (int i = 0; i < m->material_count; ++i) {
    const GltfMaterial *g = &m->materials[i];
    LubGltfMaterial *v = &e->gltf_mats[i];
    memcpy(v->base_color_factor, g->base_color_factor,
           sizeof(v->base_color_factor));
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
}

// 全 loader 共通の refresh。戻り値は entry (parsed_ok なら中身が使える) か
// NULL。r には status / version / error が入る。
static IoEntry *io_refresh(App *app, LubStr path_s, IoKind kind,
                           LubIoResult *r) {
  char path[1024];
  if (!lub_str_copy(path_s, path, sizeof(path))) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "path too long");
    return NULL;
  }
  IoEntry *e = io_entry_get(app, path, kind, true);
  if (!e) {
    io_result(r, LUB_IO_STATUS_ERROR, 0, "out of memory");
    return NULL;
  }
  int64_t mtime = app_file_mtime_ns(path);
  if (mtime == 0) {
    io_result_request(app, r, path, 0);
    return NULL;
  }
  if (e->parsed_ok && e->mtime == mtime) {
    io_result(r, LUB_IO_STATUS_READY, version_of(e->hash), NULL);
    return e;
  }
  size_t len = 0;
  uint8_t *bytes = read_file(path, &len);
  if (!bytes) {
    io_result_request(app, r, path, e->parsed_ok ? version_of(e->hash) : 0);
    return e->parsed_ok ? e : NULL;
  }
  uint64_t hash = lub_io_fnv1a64(bytes, len);
  if (e->parsed_ok && e->hash == hash) {
    e->mtime = mtime; // 内容変わってない、mtime だけ更新
    free(bytes);
    io_result(r, LUB_IO_STATUS_READY, version_of(hash), NULL);
    return e;
  }

  static char err[512];
  err[0] = '\0';
  switch (kind) {
  case IO_TEXT: {
    io_entry_free_parsed(e);
    e->bytes = bytes;
    e->bytes_len = len;
    bytes = NULL;
    e->parsed_ok = true;
    break;
  }
  case IO_FLOATS: {
    float *f = NULL;
    int32_t n = 0;
    if (!parse_floats((const char *)bytes, len, &f, &n, err, sizeof(err))) {
      SDL_Log("io.load_floats: parse error in %s: %s", path, err);
      break;
    }
    io_entry_free_parsed(e);
    e->floats = f;
    e->float_count = n;
    e->parsed_ok = true;
    break;
  }
  case IO_GLTF: {
    // .gltf (JSON) の外部 buffer を web では先に取りにいく
    size_t pl = strlen(path);
    bool is_json = pl > 5 && strcmp(path + pl - 5, ".gltf") == 0;
    if (is_json && !gltf_deps_ready(app, path, bytes, len, r)) {
      free(bytes);
      return e->parsed_ok ? e : NULL;
    }
    GltfMesh *m = gltf_load(path, err, sizeof(err));
    if (!m) {
      SDL_Log("%s", err);
      break;
    }
    io_entry_free_parsed(e);
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
    io_entry_free_parsed(e);
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
    // parse 失敗: cache を更新せず前回値を維持
    io_result(r, LUB_IO_STATUS_ERROR, e->parsed_ok ? version_of(e->hash) : 0,
              err);
    return e->parsed_ok ? e : NULL;
  }
  e->mtime = mtime;
  e->hash = hash;
  io_result(r, LUB_IO_STATUS_READY, version_of(hash), NULL);
  return e;
}

LubStatus lub_io_load_text(LubContext *ctx, LubStr path, LubView *text,
                           LubIoResult *r) {
  App *app = lub_api_app(ctx);
  memset(text, 0, sizeof(*text));
  IoEntry *e = io_refresh(app, path, IO_TEXT, r);
  if (e) {
    text->ptr = e->bytes;
    text->len = (int32_t)e->bytes_len;
    text->frame = (int32_t)app->frame_index;
  }
  return LUB_OK;
}

LubStatus lub_io_load_floats(LubContext *ctx, LubStr path, const float **data,
                             int32_t *count, LubIoResult *r) {
  App *app = lub_api_app(ctx);
  *data = NULL;
  *count = 0;
  IoEntry *e = io_refresh(app, path, IO_FLOATS, r);
  if (e) {
    *data = e->floats;
    *count = e->float_count;
  }
  return LUB_OK;
}

LubStatus lub_io_load_gltf(LubContext *ctx, LubStr path, LubGltfView *mesh,
                           LubIoResult *r) {
  App *app = lub_api_app(ctx);
  memset(mesh, 0, sizeof(*mesh));
  IoEntry *e = io_refresh(app, path, IO_GLTF, r);
  if (e) {
    mesh->primitives = e->gltf_prims;
    mesh->primitive_count = e->gltf->primitive_count;
    mesh->materials = e->gltf_mats;
    mesh->material_count = e->gltf->material_count;
  }
  return LUB_OK;
}

LubStatus lub_png_load(LubContext *ctx, LubStr path, LubView *pixels,
                       int32_t *w, int32_t *h, int32_t *format, int32_t *stride,
                       LubIoResult *r) {
  App *app = lub_api_app(ctx);
  memset(pixels, 0, sizeof(*pixels));
  *w = 0;
  *h = 0;
  *format = 0;
  *stride = 0;
  IoEntry *e = io_refresh(app, path, IO_PNG, r);
  if (e) {
    pixels->ptr = e->pixels;
    pixels->len = e->stride * e->h;
    pixels->frame = (int32_t)app->frame_index;
    *w = e->w;
    *h = e->h;
    *format = LUB_GFX_PIXEL_FORMAT_RGBA8;
    *stride = e->stride;
  }
  return LUB_OK;
}

LubStatus lub_png_write(LubContext *ctx, LubStr path, const uint8_t *pixels,
                        int32_t len, int32_t w, int32_t h, int32_t stride) {
  App *app = lub_api_app(ctx);
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

static int32_t layout_stride(int32_t layout) {
  switch (layout) {
  case LUB_MESH_LAYOUT_PN:
    return 6;
  case LUB_MESH_LAYOUT_PNU:
    return 8;
  case LUB_MESH_LAYOUT_PNUT:
    return 12;
  case LUB_MESH_LAYOUT_PNCM:
    return 11;
  case LUB_MESH_LAYOUT_PNCMW:
    return 15;
  default:
    return 0;
  }
}

int32_t lub_mesh_interleave(LubContext *ctx, const LubMeshData *mesh,
                            int32_t layout, float *out, int32_t cap) {
  (void)ctx;
  int32_t stride = layout_stride(layout);
  if (!mesh || stride == 0 || mesh->vert_count < 0 || !mesh->positions)
    return 0;
  int32_t n = mesh->vert_count;
  int32_t need = n * stride;
  if (!out || cap < need)
    return need;
  float *o = out;
  for (int32_t i = 0; i < n; ++i) {
    const float *p = mesh->positions + i * 3;
    *o++ = p[0];
    *o++ = p[1];
    *o++ = p[2];
    if (mesh->normals) {
      const float *nm = mesh->normals + i * 3;
      *o++ = nm[0];
      *o++ = nm[1];
      *o++ = nm[2];
    } else {
      *o++ = 0;
      *o++ = 0;
      *o++ = 1;
    }
    if (layout == LUB_MESH_LAYOUT_PNU || layout == LUB_MESH_LAYOUT_PNUT) {
      if (mesh->uvs) {
        *o++ = mesh->uvs[i * 2];
        *o++ = mesh->uvs[i * 2 + 1];
      } else {
        *o++ = 0;
        *o++ = 0;
      }
    }
    if (layout == LUB_MESH_LAYOUT_PNUT) {
      // tangent 欠損時は w=0 にして shader 側が derivative TBN に fallback する
      if (mesh->tangents) {
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
      // colors 欠損は 0.8 グレー、metal_rough 欠損は (0, 0.8)
      if (mesh->colors) {
        const float *c = mesh->colors + i * 3;
        *o++ = c[0];
        *o++ = c[1];
        *o++ = c[2];
      } else {
        *o++ = 0.8f;
        *o++ = 0.8f;
        *o++ = 0.8f;
      }
      if (mesh->metal_rough) {
        *o++ = mesh->metal_rough[i * 2];
        *o++ = mesh->metal_rough[i * 2 + 1];
      } else {
        *o++ = 0;
        *o++ = 0.8f;
      }
    }
    if (layout == LUB_MESH_LAYOUT_PNCMW) {
      // joints/weights 欠損は「bone 0 に重み 1」
      if (mesh->joints && mesh->weights) {
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
