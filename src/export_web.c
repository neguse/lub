// export_web.c — the page for `lub export web`: write into the snapshot an
// index.html that embeds the snapshot and the whole web player. This single
// file runs even where external files cannot be loaded (claude.ai artifacts).
//
// What is embedded: the tcs VM player (lub.js / lub.wasm in wasm/vm/) and the
// snapshot files, concatenated, compressed with zlib, and base64-encoded, plus
// their index. The page's loader decompresses it with DecompressionStream,
// writes the snapshot to MEMFS, and starts the player with the argument passed
// to it (<Entry>.tcsb).
#include "backend.h"
#include <SDL3/SDL.h>
#include <stdlib.h>
#include <string.h>

// zlib compression from stb_image_write.h (src/stb_impl.c).
// Release with free.
unsigned char *stbi_zlib_compress(unsigned char *data, int data_len,
                                  int *out_len, int quality);

static const char *const k_player_files[] = {"lub.js", "lub.wasm"};

typedef struct Buf {
  char *p;
  size_t len, cap;
} Buf;

static bool buf_add(Buf *b, const void *data, size_t n) {
  if (b->len + n > b->cap) {
    size_t cap = b->cap ? b->cap : 1 << 20;
    while (cap < b->len + n)
      cap *= 2;
    char *p = (char *)SDL_realloc(b->p, cap);
    if (!p)
      return false;
    b->p = p;
    b->cap = cap;
  }
  memcpy(b->p + b->len, data, n);
  b->len += n;
  return true;
}

static bool buf_str(Buf *b, const char *s) { return buf_add(b, s, strlen(s)); }

static bool has_player(const char *dir) {
  for (size_t i = 0; i < SDL_arraysize(k_player_files); ++i) {
    char path[1200];
    SDL_snprintf(path, sizeof(path), "%s/%s", dir, k_player_files[i]);
    if (!SDL_GetPathInfo(path, NULL))
      return false;
  }
  return true;
}

// Directory of the web player: wasm/vm next to the executable (release), or
// build/wasm/vm under the lub root (dev builds where the executable is in
// build-*/).
static bool find_player(char *out, size_t cap) {
  const char *base = SDL_GetBasePath();
  if (!base)
    return false;
  SDL_snprintf(out, cap, "%swasm/vm", base);
  if (has_player(out))
    return true;
  SDL_snprintf(out, cap, "%s../build/wasm/vm", base);
  return has_player(out);
}

static int compare_paths(const void *a, const void *b) {
  return strcmp(*(char *const *)a, *(char *const *)b);
}

// Append the file named name to data and write [name, offset, length] to the
// index.
static bool add_file(Buf *data, Buf *index, const char *name,
                     const char *path) {
  if (strpbrk(name, "\"\\<")) {
    SDL_Log("export: %s: the page cannot carry a path with \", \\ or <", name);
    return false;
  }
  size_t len = 0;
  void *bytes = SDL_LoadFile(path, &len);
  if (!bytes) {
    SDL_Log("export: read failed: %s: %s", path, SDL_GetError());
    return false;
  }
  char entry[1400];
  SDL_snprintf(entry, sizeof(entry), "%s[\"%s\",%zu,%zu]",
               index->len > 1 ? "," : "", name, data->len, len);
  bool ok = buf_add(data, bytes, len) && buf_str(index, entry);
  SDL_free(bytes);
  return ok;
}

static bool base64(Buf *out, const unsigned char *in, size_t n) {
  static const char k[] =
      "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  char quad[4];
  for (size_t i = 0; i < n; i += 3) {
    uint32_t v = (uint32_t)in[i] << 16;
    if (i + 1 < n)
      v |= (uint32_t)in[i + 1] << 8;
    if (i + 2 < n)
      v |= in[i + 2];
    quad[0] = k[(v >> 18) & 63];
    quad[1] = k[(v >> 12) & 63];
    quad[2] = i + 1 < n ? k[(v >> 6) & 63] : '=';
    quad[3] = i + 2 < n ? k[v & 63] : '=';
    if (!buf_add(out, quad, 4))
      return false;
  }
  return true;
}

// The page's loader.
// Create the WebGPU device the player needs up front and pass it in (same as
// web/player/main.ts).
static const char k_loader_head[] =
    "(async () => {\n"
    "  const status = document.getElementById(\"status\");\n"
    "  try {\n"
    "    const b64 = document.getElementById(\"lub-data\").textContent;\n"
    "    const bin = atob(b64);\n"
    "    const z = new Uint8Array(bin.length);\n"
    "    for (let i = 0; i < bin.length; i++) z[i] = bin.charCodeAt(i);\n"
    "    const all = new Uint8Array(await new Response(new Blob([z]).stream()"
    ".pipeThrough(new DecompressionStream(\"deflate\"))).arrayBuffer());\n"
    "    const files = {};\n"
    "    for (const [name, off, len] of JSON.parse(document.getElementById("
    "\"lub-index\").textContent))\n"
    "      files[name] = all.subarray(off, off + len);\n"
    "    const url = (name, type) => URL.createObjectURL(new Blob([files[name]]"
    ", { type }));\n"
    "    if (!navigator.gpu) throw new Error(\"this browser has no WebGPU\");\n"
    "    const adapter = await navigator.gpu.requestAdapter();\n"
    "    if (!adapter) throw new Error(\"no WebGPU adapter\");\n"
    "    const features = [\"depth32float-stencil8\", \"float32-filterable\", "
    "\"timestamp-query\"]"
    ".filter((f) => adapter.features.has(f));\n"
    "    const device = await adapter.requestDevice({ requiredFeatures: "
    "features });\n"
    "    const M = (window.Module = {\n"
    "      canvas: document.getElementById(\"canvas\"),\n"
    "      preinitializedWebGPUDevice: device,\n"
    "      arguments: [\"";
static const char k_loader_tail[] =
    "\"],\n"
    "      locateFile: (p) => url(\"player/\" + p, p.endsWith(\".wasm\") ? "
    "\"application/wasm\" : \"application/octet-stream\"),\n"
    "      print: (t) => console.log(t),\n"
    "      printErr: (t) => console.error(t),\n"
    "      preRun: [() => {\n"
    "        for (const name in files) {\n"
    "          if (!name.startsWith(\"game/\")) continue;\n"
    "          const path = \"/\" + name.slice(5);\n"
    "          const dir = path.slice(0, path.lastIndexOf(\"/\"));\n"
    "          if (dir) M.FS.mkdirTree(dir);\n"
    "          M.FS.writeFile(path, files[name]);\n"
    "        }\n"
    "      }],\n"
    "    });\n"
    "    const s = document.createElement(\"script\");\n"
    "    s.src = url(\"player/lub.js\", \"text/javascript\");\n"
    "    document.body.appendChild(s);\n"
    "    status.textContent = \"\";\n"
    "  } catch (e) {\n"
    "    status.textContent = \"lub failed to start: \" + (e && e.message ? "
    "e.message : e);\n"
    "  }\n"
    "})();\n";

bool export_write_web_page(const char *out_dir, const char *title,
                           const char *start) {
  char player[1100];
  if (!find_player(player, sizeof(player))) {
    SDL_Log("export: the web player (lub.js, lub.wasm) is neither in wasm/vm/ "
            "next to lub nor in build/wasm/vm/ of the lub checkout");
    return false;
  }
  if (strpbrk(start, "\"\\<")) {
    SDL_Log("export: %s: the page cannot start a path with \", \\ or <", start);
    return false;
  }
  Buf data = {0}, index = {0}, page = {0};
  bool ok = buf_str(&index, "[");
  for (size_t i = 0; ok && i < SDL_arraysize(k_player_files); ++i) {
    char name[64], path[1200];
    SDL_snprintf(name, sizeof(name), "player/%s", k_player_files[i]);
    SDL_snprintf(path, sizeof(path), "%s/%s", player, k_player_files[i]);
    ok = add_file(&data, &index, name, path);
  }
  int count = 0;
  char **paths = ok ? SDL_GlobDirectory(out_dir, NULL, 0, &count) : NULL;
  if (ok && !paths) {
    SDL_Log("export: %s: %s", out_dir, SDL_GetError());
    ok = false;
  }
#ifdef _WIN32
  // SDL_GlobDirectory on Windows returns \ as the separator. Names in the page
  // use / as the separator, and the order also matches other OSes
  for (int i = 0; i < count; ++i)
    for (char *c = paths[i]; *c; ++c)
      if (*c == '\\')
        *c = '/';
#endif
  if (paths)
    qsort(paths, (size_t)count, sizeof(*paths), compare_paths);
  for (int i = 0; ok && i < count; ++i) {
    char path[1200], name[1100];
    SDL_PathInfo info;
    SDL_snprintf(path, sizeof(path), "%s/%s", out_dir, paths[i]);
    if (!SDL_GetPathInfo(path, &info) || info.type != SDL_PATHTYPE_FILE)
      continue;
    SDL_snprintf(name, sizeof(name), "game/%s", paths[i]);
    ok = add_file(&data, &index, name, path);
  }
  SDL_free(paths);
  ok = ok && buf_str(&index, "]");

  int zlen = 0;
  unsigned char *z =
      ok ? stbi_zlib_compress((unsigned char *)data.p, (int)data.len, &zlen, 8)
         : NULL;
  ok = z != NULL;
  ok = ok && buf_str(&page, "<!doctype html>\n<html>\n<head>\n<meta "
                            "charset=\"utf-8\">\n<meta name=\"viewport\" "
                            "content=\"width=device-width,initial-scale=1\">\n"
                            "<title>");
  for (const char *c = title; ok && *c; ++c) {
    if (!strchr("<>&\"", *c))
      ok = buf_add(&page, c, 1);
  }
  ok =
      ok &&
      buf_str(&page,
              "</title>\n<style>\n"
              "  html, body { height: 100%; margin: 0; }\n"
              "  body { background: #07090c; color: #b9c2cc; font: 13px "
              "system-ui, sans-serif; display: flex; flex-direction: column; "
              "align-items: center; justify-content: center; padding: 0 16px; "
              "box-sizing: border-box; }\n"
              "  canvas { display: block; max-width: 100%; max-height: 90%; }\n"
              "  #status { white-space: pre-wrap; margin: 8px 0; }\n"
              "</style>\n</head>\n<body>\n"
              "<canvas id=\"canvas\" width=\"480\" height=\"360\" "
              "tabindex=\"0\"></canvas>\n<div id=\"status\">loading</div>\n"
              "<script type=\"application/json\" id=\"lub-index\">");
  ok = ok && buf_add(&page, index.p, index.len);
  ok = ok && buf_str(&page, "</script>\n<script type=\"text/plain\" "
                            "id=\"lub-data\">");
  ok = ok && base64(&page, z, (size_t)zlen);
  ok = ok && buf_str(&page, "</script>\n<script>\n");
  ok = ok && buf_str(&page, k_loader_head) && buf_str(&page, start) &&
       buf_str(&page, k_loader_tail);
  ok = ok && buf_str(&page, "</script>\n</body>\n</html>\n");
  free(z);
  if (ok) {
    char path[1100];
    SDL_snprintf(path, sizeof(path), "%s/index.html", out_dir);
    ok = SDL_SaveFile(path, page.p, page.len);
    if (!ok)
      SDL_Log("export: write failed: %s: %s", path, SDL_GetError());
  } else {
    SDL_Log("export: could not build the web page");
  }
  SDL_free(data.p);
  SDL_free(index.p);
  SDL_free(page.p);
  return ok;
}
