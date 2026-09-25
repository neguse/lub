// draw の bindings を runtime が持ち、shader に結びつけるための部品。
// pass の bindings (PassOpts.Bindings) と draw state (use_draw_state) が
// bindings の写しを持ち、draw ごとに shader の reflection の名前へ結びつける。
// 結びつけは shader を compile するたびに作る名前の表 (ShaderNames) で引く。
#pragma once
#include "lub/lub_api.h"
#include "shader.h"
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// uniform block 1 つの float の上限 (draw / dispatch が詰める像の大きさ)。
enum { UB_MAX_FLOATS = 512 };

// ------------------------------------------------------------ shader names

// shader の reflection の名前を引く表。use_shader が compile するたびに作る。
// 名前ごとに、同じ名前の uniform の member (block ごとに最大 1 つ。VS と FS の
// block が同じ名前の member を持てる)、texture、StructuredBuffer の
// reflection の index を持つ。
typedef struct ShaderNames ShaderNames;

ShaderNames *shader_names_build(const ShaderReflection *refl);
void shader_names_free(ShaderNames *names);

// ------------------------------------------------------------ bind set

// bindings (LubBinding の列) の写し。名前と uniform の値を 1 つの確保に
// 写すので、元の memory (Lua の文字列、呼び出しの間だけの arena) が消えても
// 使える。buffer / texture は handle のまま持ち、使うたびに引く。
typedef struct BindSet {
  LubBinding *items;
  int32_t count;
  int32_t n_uniforms; // handle が 0 の項目 (uniform の値) の数
  void *mem;          // items と名前と値の実体
  size_t mem_cap;
} BindSet;

// b[0..n) を写す (前の内容は捨て、確保は使い回す)。確保できなければ false で、
// s は空になる。
bool bindset_copy(BindSet *s, const LubBinding *b, int32_t n);
void bindset_clear(BindSet *s);
void bindset_free(BindSet *s);

// ------------------------------------------------------------ bind layout

// bindings の資源の項目 1 つの束縛先 (shader の reflection の index)。名前が
// 同じなら同じ index になるので、段 (pass / draw state / draw) の優先はこの
// index ごとに決まる。
typedef struct BindTarget {
  int8_t tex;   // refl->texs の index (-1 = 無し)
  int8_t sbuf;  // refl->storage_bufs の index (-1 = 無し。"indices" も -1)
  bool indices; // 名前が "indices" (index buffer)
} BindTarget;

// uniform の値 1 つを block の像に書く手順。member の範囲 (size) を 0 で
// 埋めてから values の先頭 copy 個を写す。上の段が同じ member を書けば、
// 下の段の値は残らない。
typedef struct UniformWrite {
  const float *values;
  uint16_t block;  // refl->ubs の index
  uint16_t offset; // block の中の位置 (float)
  uint16_t size;   // member の float の数
  uint16_t copy;   // values から写す数 (size 以下)
} UniformWrite;

BindTarget bind_target(const ShaderNames *names, LubStr name);
// uniform の項目 b を書き込みにする (out は SGL_MAX_UNIFORM_BLOCKS 個まで)。
// 書き込みの数を返す (shader に無い名前は 0)。
int uniform_resolve(const ShaderNames *names, const ShaderReflection *refl,
                    const LubBinding *b, UniformWrite *out);
void uniform_writes_apply(const UniformWrite *w, int32_t n,
                          float (*blocks)[UB_MAX_FLOATS]);

// BindSet を 1 つの shader に結びつけたもの。shader の handle と gen (backend
// の shader を作り直すたびに増える) が変わったら作り直す。
typedef struct BindLayout {
  bool valid;
  LubHandle shader;
  uint32_t gen;
  BindTarget *targets; // 項目ごと (uniform の項目は使わない)
  UniformWrite *writes;
  int32_t n_writes;
  int32_t cap; // targets の容量 (writes は SGL_MAX_UNIFORM_BLOCKS 倍)
} BindLayout;

// s を shader に結びつけ直す。確保できなければ false (l は無効)。
bool bind_layout_resolve(BindLayout *l, const BindSet *s, LubHandle shader,
                         uint32_t gen, const ShaderNames *names,
                         const ShaderReflection *refl);
static inline bool bind_layout_fresh(const BindLayout *l, LubHandle shader,
                                     uint32_t gen) {
  return l->valid && l->shader == shader && l->gen == gen;
}
void bind_layout_free(BindLayout *l);

// ------------------------------------------------------------ draw state

// draw の pipeline の設定 (DrawOpts の省略を既定で埋めたもの)。
typedef struct DrawPipe {
  int32_t blend, cull, prim;
  bool depth_test, depth_write;
} DrawPipe;

// use_draw_state の中身 (resource table の RES_DRAW_STATE の entry が持つ)。
typedef struct DrawState {
  LubHandle shader;
  DrawPipe pipe;
  bool has_instance_count;
  int32_t instance_count;
  BindSet fixed;     // 固定の bindings
  BindLayout layout; // fixed を shader に結びつけたもの
} DrawState;

void draw_state_free(DrawState *ds);
