#pragma once

#include <stdint.h>

typedef enum {
  SGL_BUFFER_INDEX = 2,
  SGL_BUFFER_UNIFORM,
  SGL_BUFFER_STORAGE,
} SglBufferType;

typedef enum {
  SGL_PF_RGBA8 = 1,
  SGL_PF_R8,
  SGL_PF_RG8,
  SGL_PF_R16F,
  SGL_PF_RG16F,
  SGL_PF_R32F,
  SGL_PF_RGBA16F,
  SGL_PF_RGBA32F,
  SGL_PF_DEPTH16,
  SGL_PF_DEPTH24_STENCIL8,
  SGL_PF_DEPTH32F,
  SGL_PF_BGRA8,
  SGL_PF_RGBA8_SRGB,
} SglPixelFormat;

typedef enum {
  SGL_LOAD_CLEAR = 1,
  SGL_LOAD_LOAD,
  SGL_LOAD_DONTCARE
} SglLoadAction;
typedef enum { SGL_STORE_STORE = 1, SGL_STORE_DONTCARE } SglStoreAction;
typedef enum {
  SGL_BLEND_ZERO = 1,
  SGL_BLEND_ONE,
  SGL_BLEND_SRC,
  SGL_BLEND_ONE_MINUS_SRC,
  SGL_BLEND_SRC_ALPHA,
  SGL_BLEND_ONE_MINUS_SRC_ALPHA,
  SGL_BLEND_DST,
  SGL_BLEND_ONE_MINUS_DST,
  SGL_BLEND_DST_ALPHA,
  SGL_BLEND_ONE_MINUS_DST_ALPHA,
} SglBlendFactor;
typedef enum {
  SGL_BLEND_OP_ADD = 1,
  SGL_BLEND_OP_SUBTRACT,
  SGL_BLEND_OP_REVERSE_SUBTRACT,
  SGL_BLEND_OP_MIN,
  SGL_BLEND_OP_MAX,
} SglBlendOp;
// 描いた値と target の値の混ぜ方 (stub の Blend)。
// enabled が 0 なら混ぜずに上書きし、ほかの field は 0。
// 値は SglBlendFactor / SglBlendOp で、pipeline の key に memcmp で入るので padding を持たない。
typedef struct SglBlend {
  uint8_t enabled;
  uint8_t src, dst, op;
  uint8_t src_alpha, dst_alpha, op_alpha;
} SglBlend;
typedef enum { SGL_CULL_NONE = 1, SGL_CULL_BACK, SGL_CULL_FRONT } SglCull;
typedef enum {
  SGL_PRIM_TRIANGLES = 1,
  SGL_PRIM_TRIANGLE_STRIP,
  SGL_PRIM_LINES,
  SGL_PRIM_LINE_STRIP,
  SGL_PRIM_POINTS,
} SglPrimitive;
typedef enum { SGL_FILTER_LINEAR = 1, SGL_FILTER_NEAREST } SglFilter;
typedef enum { SGL_WRAP_REPEAT = 1, SGL_WRAP_CLAMP } SglWrap;
