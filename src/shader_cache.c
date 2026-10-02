#include "shader_cache.h"
#include <SDL3/SDL.h>
#include <stdint.h>
#include <stdlib.h>
#include <string.h>

// key に畳む生成器の版。Slang の版 (CMake が渡す) と、shader.cpp の後処理や
// このファイル形式を変えたら上げる revision。
#ifndef LUB_SLANG_VERSION
#define LUB_SLANG_VERSION "unknown"
#endif
#define SHADER_CACHE_GENERATOR "slang-" LUB_SLANG_VERSION "/1"
#define SHADER_CACHE_MAGIC 0x5342554cu // "LUBS"
#define SHADER_CACHE_DEFAULT_DIR "shader-cache"

typedef struct ShaderCacheHeader {
  uint32_t magic;
  uint32_t refl_bytes;
  uint32_t n_blobs;
} ShaderCacheHeader;

static uint64_t fnv1a(uint64_t h, const void *data, size_t n) {
  const uint8_t *p = (const uint8_t *)data;
  for (size_t i = 0; i < n; ++i) {
    h ^= p[i];
    h *= 0x100000001b3ull;
  }
  return h;
}

void shader_cache_key(ShaderTargetBackend target, const char *const *sources,
                      int n_sources, char out[SHADER_CACHE_KEY_CHARS]) {
  uint64_t h = 0xcbf29ce484222325ull;
  h = fnv1a(h, SHADER_CACHE_GENERATOR, sizeof(SHADER_CACHE_GENERATOR));
  uint32_t t = (uint32_t)target;
  h = fnv1a(h, &t, sizeof(t));
  for (int i = 0; i < n_sources; ++i) {
    // 終端の NUL も含めて、ソースの境界を hash に入れる
    h = fnv1a(h, sources[i], strlen(sources[i]) + 1);
  }
  SDL_snprintf(out, SHADER_CACHE_KEY_CHARS, "%016llx", (unsigned long long)h);
}

static bool cache_path(const char *key, bool for_write, char *out, size_t cap) {
  const char *dir = SDL_getenv("LUB_SHADER_CACHE");
  if (!dir || !*dir) {
    if (for_write)
      return false;
    dir = SHADER_CACHE_DEFAULT_DIR;
  }
  if (for_write && !SDL_CreateDirectory(dir))
    return false;
  SDL_snprintf(out, cap, "%s/%s.lubshader", dir, key);
  return true;
}

bool shader_cache_load(const char *key, ShaderBlob *const *blobs, int n_sources,
                       ShaderReflection *out_refl) {
  char path[1024];
  if (!cache_path(key, false, path, sizeof(path)))
    return false;
  size_t size = 0;
  uint8_t *data = (uint8_t *)SDL_LoadFile(path, &size);
  if (!data)
    return false;
  bool ok = false;
  ShaderCacheHeader h;
  size_t pos = sizeof(h);
  if (size < sizeof(h))
    goto done;
  memcpy(&h, data, sizeof(h));
  if (h.magic != SHADER_CACHE_MAGIC ||
      h.refl_bytes != sizeof(ShaderReflection) ||
      h.n_blobs != (uint32_t)n_sources || size - pos < h.refl_bytes)
    goto done;
  memcpy(out_refl, data + pos, sizeof(ShaderReflection));
  pos += h.refl_bytes;
  for (int i = 0; i < n_sources; ++i) {
    uint32_t bytes = 0;
    if (size - pos < sizeof(bytes))
      goto done;
    memcpy(&bytes, data + pos, sizeof(bytes));
    pos += sizeof(bytes);
    if (size - pos < bytes)
      goto done;
    // MSL / WGSL は text なので NUL 終端を足しておく
    blobs[i]->spirv = (uint32_t *)malloc((size_t)bytes + 1);
    if (!blobs[i]->spirv)
      goto done;
    memcpy(blobs[i]->spirv, data + pos, bytes);
    ((char *)blobs[i]->spirv)[bytes] = '\0';
    blobs[i]->bytes = bytes;
    pos += bytes;
  }
  ok = true;
done:
  if (!ok) {
    SDL_Log("shader cache: ignoring unreadable entry %s", path);
    for (int i = 0; i < n_sources; ++i)
      shader_blob_free(blobs[i]);
  }
  SDL_free(data);
  return ok;
}

void shader_cache_store(const char *key, ShaderBlob *const *blobs,
                        int n_sources, const ShaderReflection *refl) {
  char path[1024];
  if (!cache_path(key, true, path, sizeof(path)))
    return;
  size_t size = sizeof(ShaderCacheHeader) + sizeof(ShaderReflection);
  for (int i = 0; i < n_sources; ++i)
    size += sizeof(uint32_t) + blobs[i]->bytes;
  uint8_t *data = (uint8_t *)malloc(size);
  if (!data)
    return;
  ShaderCacheHeader h = {SHADER_CACHE_MAGIC, (uint32_t)sizeof(ShaderReflection),
                         (uint32_t)n_sources};
  size_t pos = 0;
  memcpy(data + pos, &h, sizeof(h));
  pos += sizeof(h);
  memcpy(data + pos, refl, sizeof(ShaderReflection));
  pos += sizeof(ShaderReflection);
  for (int i = 0; i < n_sources; ++i) {
    uint32_t bytes = (uint32_t)blobs[i]->bytes;
    memcpy(data + pos, &bytes, sizeof(bytes));
    pos += sizeof(bytes);
    memcpy(data + pos, blobs[i]->spirv, bytes);
    pos += bytes;
  }
  if (!SDL_SaveFile(path, data, size))
    SDL_Log("shader cache: write failed: %s: %s", path, SDL_GetError());
  free(data);
}
