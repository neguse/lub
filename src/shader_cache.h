#pragma once
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// Shader part of the generated cache.
// Stores the shader compile output (blob and reflection) as 1 key = 1 file,
// looked up by the hash of "generator version, target, source". No separate
// conversion mechanism for distribution is built; export also fills it through
// the same compile path as at runtime (a separate mechanism would duplicate the
// runtime generation).
//
// Where it reads: the directory set by shader_cache_set_dir if any (the export
// snapshot), otherwise cache/shader/ under cwd. It writes only when
// shader_cache_set_dir is set (export writes on every compile). For players
// without a generator (LUB_NO_SLANG, web pages that do not load slang-wasm)
// this is required data, and a miss is an error naming the key.

#define SHADER_CACHE_KEY_CHARS 17 // 16 hex + NUL

void shader_cache_set_dir(const char *dir);

void shader_cache_key(ShaderTargetBackend target, const char *const *sources,
                      int n_sources, char out[SHADER_CACHE_KEY_CHARS]);

// On a hit, fills blobs (n_sources of them; the caller calls shader_blob_free)
// and reflection, and returns true.
bool shader_cache_load(const char *key, ShaderBlob *const *blobs, int n_sources,
                       ShaderReflection *out_refl);

void shader_cache_store(const char *key, ShaderBlob *const *blobs,
                        int n_sources, const ShaderReflection *refl);

#ifdef __cplusplus
}
#endif
