#pragma once
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

// 生成キャッシュ (docs/log/2026-07-23-packaging-design.md) のシェーダ分。
// shader compile の出力 (blob と reflection) を「生成器の版、target、
// ソース」の hash で引ける 1 key = 1 file として保存する。
//
// 読む場所: env LUB_SHADER_CACHE、無ければ cwd の shader-cache/。
// 書くのは env LUB_SHADER_CACHE がある時だけ (生成器を持つ player が
// compile のたびに write-through する)。生成器を持たない player
// (LUB_NO_SLANG) にとっては必須データで、miss は key を名指しする error。

#define SHADER_CACHE_KEY_CHARS 17 // 16 hex + NUL

void shader_cache_key(ShaderTargetBackend target, const char *const *sources,
                      int n_sources, char out[SHADER_CACHE_KEY_CHARS]);

// hit なら blobs (n_sources 個、呼び出し側が shader_blob_free する) と
// reflection を埋めて true。
bool shader_cache_load(const char *key, ShaderBlob *const *blobs, int n_sources,
                       ShaderReflection *out_refl);

void shader_cache_store(const char *key, ShaderBlob *const *blobs,
                        int n_sources, const ShaderReflection *refl);

#ifdef __cplusplus
}
#endif
