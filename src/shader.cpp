// Slang shader compile + reflection -> SPIR-V blob + ShaderReflection
//
// Compiles two source strings (vertex and fragment) written in Slang to
// SPIR-V, and returns the SPIR-V byte blobs plus a small ShaderReflection
// struct to the caller. The actual GPU shader object construction
// (sg_make_shader / SDL_CreateGPUShader / etc.) lives in the backend.
// This file is C++ because the Slang public API uses COM-like C++
// interfaces; the exposed interface (shader.h) is pure C.
//
// Emscripten loads slang-wasm via EM_ASYNC_JS; native uses the C++ Slang API.
// On wasm the JS bridge returns WGSL bytes (stashed into ShaderBlob.spirv)
// plus a reflection JSON blob — see the EM_ASYNC_JS section below.
#include "shader.h"

// Native builds link Slang unless LUB_NO_SLANG is set (iOS: no Slang there).
// Such a player can only use shaders from the shader cache — see the public
// entry points at the end of this file.
#if !defined(__EMSCRIPTEN__) && !defined(LUB_NO_SLANG)
#define LUB_HAS_SLANG 1
#include <slang-com-ptr.h>
#include <slang.h>
#endif
#ifndef __EMSCRIPTEN__
#include "shader_cache.h"
#endif

#include <regex>
#include <set>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

// Per-target shader prelude. SDL_GPU wants textures and samplers as
// VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER at a single binding; D3D12 and
// WGSL use separate texture + sampler declarations. The macros let one
// shader source compile to both layouts without per-target #ifdef.
//
//   LUB_TEXTURE2D(diffuse);          // d3d12/wgsl: Texture2D + SamplerState
//                                    // pair; sdlgpu: Sampler2D<float4>.
//   color = LUB_SAMPLE(diffuse, uv); // expands to the right Sample() call.
//
// Wasm (WGSL via slang-wasm) uses the separate form: Slang auto-lowers it
// for WGSL and the declaration shape stays consistent across native + wasm
// reflection output.
[[maybe_unused]] static const char *
prelude_for_target(ShaderTargetBackend target) {
  // LUB_SAMPLE_LOD is an explicit-LOD (level 0) sample. Textures here are never
  // mipmapped so it's equivalent to LUB_SAMPLE, but unlike implicit-LOD Sample
  // it is legal in non-uniform control flow — WGSL (WebGPU) rejects an
  // implicit-LOD textureSample after a data-dependent branch/loop, which the
  // post passes (SSAO/outline/water) hit when they sample after an early-out.
  // Native SDL_GPU/Vulkan tolerates implicit-LOD there, so a native-only run
  // passes and the pipeline only turns up invalid (black screen) on web: use
  // LUB_SAMPLE_LOD for any sample reached after a branch/loop.
  //
  // LUB_VERTEX_ID / LUB_INSTANCE_ID are the per-draw vertex and instance
  // index (vertex pulling). On SPIR-V Slang lowers SV_VertexID to
  // VertexIndex - BaseVertex, which needs the DrawParameters capability that
  // SDL_GPU never enables, so that target uses the raw builtin instead; lub
  // always draws from base 0, so both mean the same thing. DXC rejects the
  // Vulkan-only semantic names, hence the per-target spelling.
  if (target == SHADER_TARGET_SDLGPU) {
    return "#define LUB_TEXTURE2D(n) Sampler2D<float4> n\n"
           "#define LUB_SAMPLE(t, uv) t.Sample(uv)\n"
           "#define LUB_SAMPLE_LOD(t, uv) t.SampleLevel(uv, 0.0)\n"
           "#define LUB_VERTEX_ID SV_VulkanVertexID\n"
           "#define LUB_INSTANCE_ID SV_VulkanInstanceID\n";
  }
  // wasm and d3d12 use the separate texture+sampler form (D3D12 has no
  // combined image samplers; t/s registers are distinct classes).
  return "#define LUB_TEXTURE2D(n) Texture2D n; SamplerState n##_smp\n"
         "#define LUB_SAMPLE(t, uv) t.Sample(t##_smp, uv)\n"
         "#define LUB_SAMPLE_LOD(t, uv) t.SampleLevel(t##_smp, uv, 0.0)\n"
         "#define LUB_VERTEX_ID SV_VertexID\n"
         "#define LUB_INSTANCE_ID SV_InstanceID\n";
}

[[maybe_unused]] static const char *stage_label(SglShaderStage stage) {
  switch (stage) {
  case SGL_STAGE_VERTEX:
    return "vertex";
  case SGL_STAGE_FRAGMENT:
    return "fragment";
  case SGL_STAGE_COMPUTE:
    return "compute";
  default:
    return "?";
  }
}

// A separate SamplerState found by reflection, before it is paired.
struct ReflSampler {
  std::string name;
  int slot;
};

// Pair a stage's separate samplers with its textures. Every backend binds the
// sampler that comes with the bound texture (one sampler per texture, a
// combined descriptor on SPIR-V), so each sampler must belong to exactly one
// texture. LUB_TEXTURE2D(n)'s own sampler n_smp pairs with n; any other
// sampler pairs with the next texture still lacking one, in declaration
// order. A sampler left without a texture, or a texture left without a
// sampler while another texture got one by order (one sampler shared by
// several textures), can't be bound and fails the compile. `refl` must
// already hold all of the stage's textures.
[[maybe_unused]] static bool pair_samplers(ShaderReflection *refl,
                                           SglShaderStage stage,
                                           const std::vector<ReflSampler> &smps,
                                           char *err, size_t errsz) {
  auto assign = [](ShaderTexture *t, const ReflSampler &s) {
    snprintf(t->smp_name, sizeof(t->smp_name), "%s", s.name.c_str());
    t->smp_slot = s.slot;
  };
  std::vector<bool> paired(smps.size(), false);
  for (size_t i = 0; i < smps.size(); ++i) {
    for (int k = 0; k < refl->tex_count; ++k) {
      ShaderTexture *t = &refl->texs[k];
      if (t->stage == stage && t->smp_slot < 0 &&
          smps[i].name == std::string(t->name) + "_smp") {
        assign(t, smps[i]);
        paired[i] = true;
        break;
      }
    }
  }
  const ShaderTexture *by_order = nullptr;
  for (size_t i = 0; i < smps.size(); ++i) {
    if (paired[i])
      continue;
    ShaderTexture *t = nullptr;
    for (int k = 0; k < refl->tex_count && !t; ++k) {
      if (refl->texs[k].stage == stage && refl->texs[k].smp_slot < 0)
        t = &refl->texs[k];
    }
    if (!t) {
      if (err && errsz)
        snprintf(err, errsz,
                 "sampler: %s sampler '%s' has no texture to pair with; lub "
                 "binds one sampler per texture, pairing each SamplerState "
                 "with a texture in declaration order. Declare textures with "
                 "LUB_TEXTURE2D(name) and sample with LUB_SAMPLE",
                 stage_label(stage), smps[i].name.c_str());
      return false;
    }
    assign(t, smps[i]);
    if (!by_order)
      by_order = t;
  }
  if (!by_order)
    return true;
  for (int k = 0; k < refl->tex_count; ++k) {
    const ShaderTexture *t = &refl->texs[k];
    if (t->stage != stage || t->smp_slot >= 0)
      continue;
    if (err && errsz)
      snprintf(err, errsz,
               "sampler: %s texture '%s' has no sampler of its own while "
               "sampler '%s' pairs with texture '%s' by declaration order; "
               "lub binds one sampler per texture, so a sampler cannot be "
               "shared between textures. Declare each texture with "
               "LUB_TEXTURE2D(name) and sample with LUB_SAMPLE",
               stage_label(stage), t->name, by_order->smp_name, by_order->name);
    return false;
  }
  return true;
}

#ifdef LUB_HAS_SLANG
using Slang::ComPtr;
using slang::EntryPointReflection;
using slang::IBlob;
using slang::IComponentType;
using slang::IEntryPoint;
using slang::IGlobalSession;
using slang::IModule;
using slang::ISession;
using slang::ProgramLayout;
using slang::SessionDesc;
using slang::TargetDesc;
using slang::TypeLayoutReflection;
using slang::TypeReflection;
using slang::VariableLayoutReflection;

namespace {

// Lazy global session, reused across compiles.
struct GlobalSlangCtx {
  ComPtr<IGlobalSession> g;
  SlangProfileID spirv_profile = SLANG_PROFILE_UNKNOWN;
  SlangProfileID dxil_profile = SLANG_PROFILE_UNKNOWN;
};

GlobalSlangCtx g_slang;

static void configure_spirv_target(TargetDesc *target) {
  static const slang::CompilerOptionEntry opts[] = {{
      slang::CompilerOptionName::DefaultImageFormatUnknown,
      {slang::CompilerOptionValueKind::Int, 0, 0, nullptr, nullptr},
  }};
  target->format = SLANG_SPIRV;
  target->profile = g_slang.spirv_profile;
  target->compilerOptionEntries = opts;
  target->compilerOptionEntryCount = sizeof(opts) / sizeof(opts[0]);
}

// DXIL emission goes through dxcompiler.dll (shipped next to the slang DLLs;
// see the DXC fetch in CMakeLists). dxcompiler >= 1.8.2502 signs the DXIL
// itself, so no dxil.dll is needed.
static void configure_target(TargetDesc *target, ShaderTargetBackend backend) {
  if (backend == SHADER_TARGET_D3D12) {
    target->format = SLANG_DXIL;
    target->profile = g_slang.dxil_profile;
    return;
  }
  if (backend == SHADER_TARGET_METAL) {
    target->format = SLANG_METAL;
    target->lineDirectiveMode = SLANG_LINE_DIRECTIVE_MODE_NONE;
    return;
  }
  configure_spirv_target(target);
}

bool ensure_global_session() {
  if (g_slang.g)
    return true;
  if (SLANG_FAILED(slang::createGlobalSession(g_slang.g.writeRef()))) {
    return false;
  }
  // Target spirv_1_0 to match SDL_GPU's Vulkan 1.0 target-env (silences
  // VUID-VkShaderModuleCreateInfo-pCode-08737). Fallbacks for older Slang.
  g_slang.spirv_profile = g_slang.g->findProfile("spirv_1_0");
  if (g_slang.spirv_profile == SLANG_PROFILE_UNKNOWN) {
    g_slang.spirv_profile = g_slang.g->findProfile("spirv_1_5");
  }
  if (g_slang.spirv_profile == SLANG_PROFILE_UNKNOWN) {
    g_slang.spirv_profile = g_slang.g->findProfile("glsl_450");
  }
  g_slang.dxil_profile = g_slang.g->findProfile("sm_6_0");
  return true;
}

// Copy a diagnostic blob into a caller-supplied buffer (truncating).
void copy_diag(IBlob *diag, char *err_buf, size_t err_buf_size) {
  if (!err_buf || err_buf_size == 0)
    return;
  if (!diag) {
    snprintf(err_buf, err_buf_size, "(no diagnostic available)");
    return;
  }
  size_t n = diag->getBufferSize();
  if (n >= err_buf_size)
    n = err_buf_size - 1;
  memcpy(err_buf, diag->getBufferPointer(), n);
  err_buf[n] = '\0';
}

void copy_name(char *dst, size_t cap, const char *src) {
  if (cap == 0)
    return;
  if (!src) {
    dst[0] = '\0';
    return;
  }
  size_t n = strlen(src);
  if (n >= cap)
    n = cap - 1;
  memcpy(dst, src, n);
  dst[n] = '\0';
}

SglPixelFormat image_format_to_sgl(SlangImageFormat fmt) {
  switch (fmt) {
  case SLANG_IMAGE_FORMAT_rgba32f:
    return SGL_PF_RGBA32F;
  case SLANG_IMAGE_FORMAT_rgba16f:
    return SGL_PF_RGBA16F;
  case SLANG_IMAGE_FORMAT_rg16f:
    return SGL_PF_RG16F;
  case SLANG_IMAGE_FORMAT_r32f:
    return SGL_PF_R32F;
  case SLANG_IMAGE_FORMAT_r16f:
    return SGL_PF_R16F;
  case SLANG_IMAGE_FORMAT_rg8:
    return SGL_PF_RG8;
  case SLANG_IMAGE_FORMAT_r8:
    return SGL_PF_R8;
  case SLANG_IMAGE_FORMAT_rgba8:
  case SLANG_IMAGE_FORMAT_unknown:
  default:
    return SGL_PF_RGBA16F;
  }
}

static uint32_t sgl_to_spv_image_format(SglPixelFormat fmt) {
  switch (fmt) {
  case SGL_PF_RGBA32F:
    return 1; // Rgba32f
  case SGL_PF_RGBA16F:
    return 2; // Rgba16f
  case SGL_PF_R32F:
    return 3; // R32f
  case SGL_PF_RG16F:
    return 7; // Rg16f
  case SGL_PF_R16F:
    return 9; // R16f
  case SGL_PF_RG8:
    return 13; // Rg8
  case SGL_PF_R8:
    return 15; // R8
  case SGL_PF_RGBA8:
    return 4; // Rgba8
  default:
    return 2; // Rgba16f
  }
}

// Map a slang TypeReflection (vector or scalar) to GLSL component count.
int component_count_of(TypeReflection *t) {
  if (!t)
    return 0;
  auto kind = t->getKind();
  if (kind == TypeReflection::Kind::Scalar)
    return 1;
  if (kind == TypeReflection::Kind::Vector)
    return (int)t->getElementCount();
  if (kind == TypeReflection::Kind::Matrix) {
    return (int)(t->getRowCount() * t->getColumnCount());
  }
  if (kind == TypeReflection::Kind::Array) {
    // e.g. float4x4 bones[8] = 128 floats (uniform packing treats the
    // member as one flat float run)
    return (int)t->getElementCount() * component_count_of(t->getElementType());
  }
  return 0;
}

bool fill_uniform_block(VariableLayoutReflection *p, SglShaderStage stage,
                        ShaderUniformBlock *ub) {
  copy_name(ub->name, sizeof(ub->name), p->getName());
  ub->slot = (int)p->getBindingIndex();
  ub->stage = stage;
  ub->size_floats = 0;
  ub->member_count = 0;

  TypeLayoutReflection *tl = p->getTypeLayout();
  if (!tl)
    return false;
  TypeReflection *t = tl->getType();
  if (!t)
    return false;

  // For a ConstantBuffer<T>, walk into its element layout.
  TypeLayoutReflection *element = tl;
  if (t->getKind() == TypeReflection::Kind::ConstantBuffer) {
    element = tl->getElementTypeLayout();
    t = element ? element->getType() : nullptr;
  }
  if (!element || !t)
    return false;

  size_t total_bytes = element->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
  ub->size_floats = (int)((total_bytes + 3) / 4);

  if (t->getKind() != TypeReflection::Kind::Struct)
    return true;

  unsigned fc = element->getFieldCount();
  for (unsigned f = 0; f < fc && ub->member_count < SGL_MAX_UB_MEMBERS; ++f) {
    VariableLayoutReflection *fl = element->getFieldByIndex(f);
    if (!fl)
      continue;
    ShaderUniformMember *m = &ub->members[ub->member_count++];
    copy_name(m->name, sizeof(m->name), fl->getName());
    size_t off_bytes = fl->getOffset(SLANG_PARAMETER_CATEGORY_UNIFORM);
    m->offset_floats = (int)(off_bytes / 4);
    TypeReflection *mt = fl->getTypeLayout()->getType();
    m->comp_count = component_count_of(mt);
    if (m->comp_count <= 0)
      m->comp_count = 1;
  }
  return true;
}

static bool refl_ub_exists(const ShaderReflection *refl, SglShaderStage stage,
                           int slot, const char *name) {
  for (int i = 0; i < refl->ub_count; ++i) {
    const ShaderUniformBlock *u = &refl->ubs[i];
    if (u->stage == stage && u->slot == slot &&
        (!name || strcmp(u->name, name) == 0))
      return true;
  }
  return false;
}

static bool refl_tex_exists(const ShaderReflection *refl, SglShaderStage stage,
                            int slot, const char *name) {
  for (int i = 0; i < refl->tex_count; ++i) {
    const ShaderTexture *t = &refl->texs[i];
    if (t->stage == stage && t->img_slot == slot &&
        (!name || strcmp(t->name, name) == 0))
      return true;
  }
  return false;
}

static bool refl_sbuf_exists(const ShaderReflection *refl, SglShaderStage stage,
                             int slot, const char *name) {
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    const ShaderStorageBuf *b = &refl->storage_bufs[i];
    if (b->stage == stage && b->slot == slot &&
        (!name || strcmp(b->name, name) == 0))
      return true;
  }
  return false;
}

static bool refl_stex_exists(const ShaderReflection *refl, SglShaderStage stage,
                             int slot, const char *name) {
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    const ShaderStorageTexture *t = &refl->storage_texs[i];
    if (t->stage == stage && t->slot == slot &&
        (!name || strcmp(t->name, name) == 0))
      return true;
  }
  return false;
}

// --- portable buffer layouts -------------------------------------------------
// lub fills StructuredBuffer<T> from one flat float list on every target, so
// the layout Slang computes for T must equal tight packing: 4-byte scalars,
// vectors of N*4 bytes, members back to back, no implicit padding. SPIR-V and
// WGSL use std430, which pads float3 / float4 members up to 16-byte offsets
// and rounds the struct size up; DXIL packs tightly. A struct that already
// satisfies std430 when packed tightly is identical everywhere, and that is
// what this check enforces (the manual states the rule as: a float3 is
// followed by a float, and the struct size is a multiple of 16 when it holds
// float3 / float4 members, of 8 when float2 is the widest).
static size_t tight_size_of(TypeReflection *t) {
  if (!t)
    return 0;
  switch (t->getKind()) {
  case TypeReflection::Kind::Scalar:
    return 4;
  case TypeReflection::Kind::Vector:
    return 4 * (size_t)t->getElementCount();
  case TypeReflection::Kind::Matrix:
    return 4 * (size_t)t->getRowCount() * (size_t)t->getColumnCount();
  case TypeReflection::Kind::Array:
    return (size_t)t->getElementCount() * tight_size_of(t->getElementType());
  case TypeReflection::Kind::Struct: {
    size_t n = 0;
    for (unsigned i = 0; i < t->getFieldCount(); ++i)
      n += tight_size_of(t->getFieldByIndex(i)->getType());
    return n;
  }
  default:
    return 0;
  }
}

static bool check_tight_layout(TypeLayoutReflection *tl, const char *name,
                               char *err, size_t errsz) {
  TypeReflection *t = tl ? tl->getType() : nullptr;
  if (!t)
    return true;
  const char *nm = name ? name : "?";
  switch (t->getKind()) {
  case TypeReflection::Kind::Struct: {
    size_t off = 0;
    for (unsigned i = 0; i < tl->getFieldCount(); ++i) {
      VariableLayoutReflection *fl = tl->getFieldByIndex(i);
      size_t got = fl->getOffset(SLANG_PARAMETER_CATEGORY_UNIFORM);
      if (got != off) {
        if (err && errsz)
          snprintf(err, errsz,
                   "buffer layout: %s.%s sits at byte %zu on this target but "
                   "%zu when packed tightly; pad the member before it (a "
                   "float3 is followed by a float) so every target agrees",
                   nm, fl->getName() ? fl->getName() : "?", got, off);
        return false;
      }
      if (!check_tight_layout(fl->getTypeLayout(), fl->getName(), err, errsz))
        return false;
      off += tight_size_of(fl->getType());
    }
    size_t size = tl->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
    if (size != off) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: struct %s is %zu bytes on this target but %zu "
                 "when packed tightly; pad it to a multiple of 16 (8 when "
                 "float2 is the widest member)",
                 nm, size, off);
      return false;
    }
    return true;
  }
  case TypeReflection::Kind::Array: {
    size_t stride = tl->getElementStride(SLANG_PARAMETER_CATEGORY_UNIFORM);
    size_t tight = tight_size_of(t->getElementType());
    if (stride != tight) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: array %s has element stride %zu on this "
                 "target but %zu when packed tightly; use float4 (or pad) "
                 "elements",
                 nm, stride, tight);
      return false;
    }
    return check_tight_layout(tl->getElementTypeLayout(), nm, err, errsz);
  }
  default: {
    size_t size = tl->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
    size_t tight = tight_size_of(t);
    if (size != tight) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: %s is %zu bytes on this target but %zu when "
                 "packed tightly (use 32-bit scalars, float4 matrix rows)",
                 nm, size, tight);
      return false;
    }
    return true;
  }
  }
}

// Metal variant of the same rule. Slang lays Metal structs out with a 16-byte
// float3, so its reflection can't be compared against tight packing. The MSL
// is rewritten to packed_float3 instead (msl_pack_buffer_structs), which
// matches tight packing exactly when the struct is tight under std430, so the
// check computes std430 alignment from the types alone. Returns the std430
// alignment of `t` through out_align.
static bool check_tight_std430(TypeReflection *t, const char *name,
                               size_t *out_align, char *err, size_t errsz) {
  *out_align = 4;
  if (!t)
    return true;
  const char *nm = name ? name : "?";
  switch (t->getKind()) {
  case TypeReflection::Kind::Vector: {
    size_t n = t->getElementCount();
    *out_align = n <= 1 ? 4 : n == 2 ? 8 : 16;
    return true;
  }
  case TypeReflection::Kind::Matrix: {
    size_t cols = t->getColumnCount();
    size_t align = cols <= 1 ? 4 : cols == 2 ? 8 : 16;
    *out_align = align;
    if ((4 * cols) % align != 0) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: %s is %zu bytes on this target but %zu when "
                 "packed tightly (use 32-bit scalars, float4 matrix rows)",
                 nm, (size_t)t->getRowCount() * align, tight_size_of(t));
      return false;
    }
    return true;
  }
  case TypeReflection::Kind::Array: {
    TypeReflection *el = t->getElementType();
    if (!check_tight_std430(el, nm, out_align, err, errsz))
      return false;
    size_t tight = tight_size_of(el);
    size_t stride = (tight + *out_align - 1) / *out_align * *out_align;
    if (stride != tight) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: array %s has element stride %zu on this "
                 "target but %zu when packed tightly; use float4 (or pad) "
                 "elements",
                 nm, stride, tight);
      return false;
    }
    return true;
  }
  case TypeReflection::Kind::Struct: {
    size_t off = 0, max_align = 4;
    for (unsigned i = 0; i < t->getFieldCount(); ++i) {
      slang::VariableReflection *f = t->getFieldByIndex(i);
      size_t align = 4;
      if (!check_tight_std430(f->getType(), f->getName(), &align, err, errsz))
        return false;
      if (off % align != 0) {
        if (err && errsz)
          snprintf(err, errsz,
                   "buffer layout: %s.%s sits at byte %zu on this target but "
                   "%zu when packed tightly; pad the member before it (a "
                   "float3 is followed by a float) so every target agrees",
                   nm, f->getName() ? f->getName() : "?",
                   (off + align - 1) / align * align, off);
        return false;
      }
      off += tight_size_of(f->getType());
      if (align > max_align)
        max_align = align;
    }
    *out_align = max_align;
    if (off % max_align != 0) {
      if (err && errsz)
        snprintf(err, errsz,
                 "buffer layout: struct %s is %zu bytes on this target but %zu "
                 "when packed tightly; pad it to a multiple of 16 (8 when "
                 "float2 is the widest member)",
                 nm, (off + max_align - 1) / max_align * max_align, off);
      return false;
    }
    return true;
  }
  default:
    return true;
  }
}

// Record one global (module-scope) shader parameter into the reflection,
// attributed to `stage`. Separate sampler states are appended to `samplers`
// for pair_samplers, so callers must feed a stage's parameters in declaration
// order.
bool fill_global_param(VariableLayoutReflection *p, ShaderReflection *out,
                       SglShaderStage stage, ShaderTargetBackend target,
                       std::vector<ReflSampler> *samplers, char *err,
                       size_t errsz) {
  {
    SlangParameterCategory cat = (SlangParameterCategory)p->getCategory();
    TypeReflection *t =
        p->getTypeLayout() ? p->getTypeLayout()->getType() : nullptr;

    // Structured / RW structured buffers. Slang reports StructuredBuffer<T>
    // under SHADER_RESOURCE (resource shape == STRUCTURED_BUFFER) and
    // RWStructuredBuffer<T> under UNORDERED_ACCESS. Both feed the same
    // storage-buffer plumbing on our backends, distinguished by `readonly`.
    // Checked before constant buffers: Metal has one [[buffer]] index space,
    // so there a structured buffer's category is CONSTANT_BUFFER too.
    bool is_structured_buf = false;
    bool readonly = true;
    if (t && t->getKind() == TypeReflection::Kind::Resource) {
      SlangResourceShape shape =
          (SlangResourceShape)(t->getResourceShape() &
                               SLANG_RESOURCE_BASE_SHAPE_MASK);
      if (shape == SLANG_STRUCTURED_BUFFER ||
          shape == SLANG_BYTE_ADDRESS_BUFFER) {
        is_structured_buf = true;
        SlangResourceAccess acc = t->getResourceAccess();
        readonly = (acc == SLANG_RESOURCE_ACCESS_READ);
      }
    }
    if (is_structured_buf) {
      if (out->storage_buf_count < SGL_MAX_STORAGE_BUFS &&
          !refl_sbuf_exists(out, stage, (int)p->getBindingIndex(),
                            p->getName())) {
        ShaderStorageBuf *sb = &out->storage_bufs[out->storage_buf_count++];
        copy_name(sb->name, sizeof(sb->name), p->getName());
        sb->slot = (int)p->getBindingIndex();
        sb->stage = stage;
        sb->readonly = readonly;
        sb->elem_stride = 0;
        if (TypeLayoutReflection *tl = p->getTypeLayout()) {
          if (TypeLayoutReflection *el = tl->getElementTypeLayout()) {
            if (target == SHADER_TARGET_METAL) {
              size_t align = 4;
              sb->elem_stride = (int)tight_size_of(el->getType());
              if (!check_tight_std430(el->getType(), p->getName(), &align, err,
                                      errsz))
                return false;
            } else {
              sb->elem_stride =
                  (int)el->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
              if (!check_tight_layout(el, p->getName(), err, errsz))
                return false;
            }
          }
        }
      }
      return true;
    }

    // Constant buffers / uniform blocks
    if (cat == SLANG_PARAMETER_CATEGORY_CONSTANT_BUFFER ||
        (t && t->getKind() == TypeReflection::Kind::ConstantBuffer)) {
      if (out->ub_count < SGL_MAX_UNIFORM_BLOCKS &&
          !refl_ub_exists(out, stage, (int)p->getBindingIndex(),
                          p->getName())) {
        fill_uniform_block(p, stage, &out->ubs[out->ub_count]);
        out->ub_count++;
      }
      return true;
    }

    // Texture resources. Read-write textures become storage textures;
    // sampled textures keep the existing texture+sampler reflection path.
    if (cat == SLANG_PARAMETER_CATEGORY_SHADER_RESOURCE ||
        cat == SLANG_PARAMETER_CATEGORY_UNORDERED_ACCESS ||
        (t && t->getKind() == TypeReflection::Kind::Resource)) {
      SlangResourceAccess acc =
          t ? t->getResourceAccess() : SLANG_RESOURCE_ACCESS_READ;
      bool storage_tex = false;
      if (t && t->getKind() == TypeReflection::Kind::Resource) {
        SlangResourceShape shape =
            (SlangResourceShape)(t->getResourceShape() &
                                 SLANG_RESOURCE_BASE_SHAPE_MASK);
        storage_tex =
            (shape == SLANG_TEXTURE_1D || shape == SLANG_TEXTURE_2D ||
             shape == SLANG_TEXTURE_3D || shape == SLANG_TEXTURE_CUBE) &&
            acc != SLANG_RESOURCE_ACCESS_READ;
      }
      if (storage_tex) {
        if (out->storage_tex_count < SGL_MAX_STORAGE_TEXTURES &&
            !refl_stex_exists(out, stage, (int)p->getBindingIndex(),
                              p->getName())) {
          ShaderStorageTexture *st =
              &out->storage_texs[out->storage_tex_count++];
          copy_name(st->name, sizeof(st->name), p->getName());
          st->slot = (int)p->getBindingIndex();
          st->stage = stage;
          st->access_format = image_format_to_sgl(p->getImageFormat());
          st->readonly = false;
        }
      } else if (out->tex_count < SGL_MAX_TEXTURES &&
                 !refl_tex_exists(out, stage, (int)p->getBindingIndex(),
                                  p->getName())) {
        ShaderTexture *tx = &out->texs[out->tex_count++];
        copy_name(tx->name, sizeof(tx->name), p->getName());
        tx->img_slot = (int)p->getBindingIndex();
        tx->stage = stage;
        // Combined `Sampler2D<>` puts the sampler at the same binding
        // as the image (single descriptor); separate `Texture2D` waits
        // for pair_samplers.
        tx->smp_name[0] = '\0';
        bool combined = false;
        if (t && t->getKind() == TypeReflection::Kind::Resource) {
          unsigned shape = (unsigned)t->getResourceShape();
          combined = (shape & SLANG_TEXTURE_COMBINED_FLAG) != 0;
        }
        tx->smp_slot = combined ? tx->img_slot : -1;
      }
      return true;
    }

    // Sampler states are paired once all of the stage's textures are known
    // (pair_samplers).
    if (cat == SLANG_PARAMETER_CATEGORY_SAMPLER_STATE ||
        (t && t->getKind() == TypeReflection::Kind::SamplerState)) {
      samplers->push_back(
          {p->getName() ? p->getName() : "", (int)p->getBindingIndex()});
      return true;
    }
  }
  return true;
}

bool fill_global_reflection(ProgramLayout *layout, ShaderReflection *out,
                            SglShaderStage stage, ShaderTargetBackend target,
                            char *err, size_t errsz) {
  if (!layout)
    return false;
  std::vector<ReflSampler> samplers;
  unsigned gpc = layout->getParameterCount();
  for (unsigned i = 0; i < gpc; ++i) {
    VariableLayoutReflection *p = layout->getParameterByIndex(i);
    if (!p)
      continue;
    if (!fill_global_param(p, out, stage, target, &samplers, err, errsz))
      return false;
  }
  return pair_samplers(out, stage, samplers, err, errsz);
}

// SDL_GPU expects a per-stage Vulkan descriptor-set layout
// (see SDL_gpu.h CreateGPUShader docs):
//   vertex stage   -> set 0: textures/storage; set 1: uniform buffers
//   fragment stage -> set 2: textures/samplers/storage; set 3: uniform buffers
//   compute stage  -> set 0: sampled textures + RO storage textures/buffers
//                     set 1: RW storage textures + RW storage buffers
//                     set 2: uniform buffers
// Slang emits everything on set 0 by default, which causes VkPipelineLayout /
// SPIR-V mismatch panics on strict drivers;
// patch_spirv_bindings_from_reflection rewrites the decorations to match.
enum class SpvStage { Vertex, Fragment, Compute };

// Renumber the binding decorations of fragment-stage texture/sampler
// resources to be 0-based in declaration order. Slang allocates binding
// indices module-wide (across vertex + fragment + uniform blocks), so an
// FS texture can land at binding 1 if the VS declares a uniform block.
// SDL_GPU's per-stage descriptor set layout exposes
//   num_samplers = N => bindings 0..N-1
// so any non-contiguous numbering trips
//   VUID-VkGraphicsPipelineCreateInfo-layout-07988.
// We also mirror the new binding back into ShaderReflection so the
// backend's name->slot lookup targets the renumbered slot.
//
// Only the FS UniformConstant variables are touched. UBs stay where Slang
// put them (they live in a separate descriptor set, and lub's samples
// never have more than one per stage so binding 0 is already correct).
[[maybe_unused]] void
renumber_fs_image_bindings_sdlgpu(ShaderBlob *fs_blob, ShaderReflection *refl) {
  if (!fs_blob || !fs_blob->spirv || fs_blob->bytes < 20)
    return;
  uint32_t *words = fs_blob->spirv;
  size_t nwords = fs_blob->bytes / 4;
  if (words[0] != 0x07230203u)
    return;

  constexpr uint32_t kOpName = 5;
  constexpr uint32_t kOpVariable = 59;
  constexpr uint32_t kOpDecorate = 71;
  constexpr uint32_t kDecBinding = 33;
  constexpr uint32_t kStorageUniformConstant = 0;

  struct VarInfo {
    uint32_t id;
    std::string name;
  };
  std::vector<VarInfo> vars;

  // Pass 1: collect UniformConstant OpVariables in declaration order.
  size_t i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpVariable && wc >= 4 &&
        words[i + 3] == kStorageUniformConstant) {
      vars.push_back({words[i + 2], ""});
    }
    i += wc;
  }
  if (vars.empty())
    return;

  // Pass 1b: pick up names via OpName so we can update the reflection
  // entry by texture name.
  i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpName && wc >= 2) {
      uint32_t target = words[i + 1];
      const char *name = (const char *)&words[i + 2];
      for (auto &v : vars) {
        if (v.id == target && v.name.empty()) {
          v.name = name;
          break;
        }
      }
    }
    i += wc;
  }

  // Pass 2: rewrite OpDecorate %id Binding ... to declaration index.
  i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpDecorate && wc >= 4 && words[i + 2] == kDecBinding) {
      uint32_t target = words[i + 1];
      for (size_t k = 0; k < vars.size(); ++k) {
        if (vars[k].id == target) {
          words[i + 3] = (uint32_t)k;
          break;
        }
      }
    }
    i += wc;
  }

  // Pass 3: mirror the new bindings into ShaderReflection.
  if (refl) {
    for (size_t k = 0; k < vars.size(); ++k) {
      if (vars[k].name.empty())
        continue;
      for (int t = 0; t < refl->tex_count; ++t) {
        if (strcmp(refl->texs[t].name, vars[k].name.c_str()) == 0) {
          refl->texs[t].img_slot = (int)k;
          refl->texs[t].smp_slot = (int)k;
          break;
        }
      }
    }
  }
}

SglShaderStage to_sgl_stage(SpvStage stage) {
  switch (stage) {
  case SpvStage::Vertex:
    return SGL_STAGE_VERTEX;
  case SpvStage::Fragment:
    return SGL_STAGE_FRAGMENT;
  case SpvStage::Compute:
    return SGL_STAGE_COMPUTE;
  }
  return SGL_STAGE_NONE;
}

static int sdl_set_for_stage_resource(SglShaderStage stage) {
  switch (stage) {
  case SGL_STAGE_VERTEX:
    return 0;
  case SGL_STAGE_FRAGMENT:
    return 2;
  case SGL_STAGE_COMPUTE:
    return 0;
  default:
    return 0;
  }
}

static int sdl_set_for_stage_uniform(SglShaderStage stage) {
  switch (stage) {
  case SGL_STAGE_VERTEX:
    return 1;
  case SGL_STAGE_FRAGMENT:
    return 3;
  case SGL_STAGE_COMPUTE:
    return 2;
  default:
    return 1;
  }
}

static int sdl_sampler_count_for_stage(const ShaderReflection *refl,
                                       SglShaderStage stage) {
  int count = 0;
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].stage == stage)
      count++;
  }
  return count;
}

static int sdl_storage_tex_count_for_stage(const ShaderReflection *refl,
                                           SglShaderStage stage,
                                           bool readonly) {
  int count = 0;
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (refl->storage_texs[i].stage == stage &&
        refl->storage_texs[i].readonly == readonly)
      count++;
  }
  return count;
}

static int sdl_read_storage_buffer_binding(const ShaderReflection *refl,
                                           SglShaderStage stage,
                                           const ShaderStorageBuf *buf) {
  return sdl_sampler_count_for_stage(refl, stage) +
         sdl_storage_tex_count_for_stage(refl, stage, true) + buf->slot;
}

static int sdl_read_storage_texture_binding(const ShaderReflection *refl,
                                            SglShaderStage stage,
                                            const ShaderStorageTexture *tex) {
  return sdl_sampler_count_for_stage(refl, stage) + tex->slot;
}

static int sdl_write_storage_buffer_binding(const ShaderReflection *refl,
                                            SglShaderStage stage,
                                            const ShaderStorageBuf *buf) {
  return sdl_storage_tex_count_for_stage(refl, stage, false) + buf->slot;
}

// A texture and its paired sampler share one combined image-sampler binding:
// SPIR-V may read a combined descriptor through separate image and sampler
// variables decorated with the same set and binding.
static bool reflected_binding_for_name(const ShaderReflection *refl,
                                       SglShaderStage stage, const char *name,
                                       int *out_set, int *out_binding) {
  if (!refl || !name || !out_set || !out_binding)
    return false;
  for (int i = 0; i < refl->ub_count; ++i) {
    const ShaderUniformBlock *u = &refl->ubs[i];
    if (u->stage == stage && strcmp(u->name, name) == 0) {
      *out_set = sdl_set_for_stage_uniform(stage);
      *out_binding = u->slot;
      return true;
    }
  }
  for (int i = 0; i < refl->tex_count; ++i) {
    const ShaderTexture *t = &refl->texs[i];
    if (t->stage != stage)
      continue;
    if (strcmp(t->name, name) == 0 ||
        (t->smp_name[0] && strcmp(t->smp_name, name) == 0)) {
      *out_set = sdl_set_for_stage_resource(stage);
      *out_binding = t->smp_slot;
      return true;
    }
  }
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    const ShaderStorageBuf *b = &refl->storage_bufs[i];
    if (b->stage == stage && strcmp(b->name, name) == 0) {
      if (stage == SGL_STAGE_COMPUTE && !b->readonly) {
        *out_set = 1;
        *out_binding = sdl_write_storage_buffer_binding(refl, stage, b);
      } else {
        *out_set = sdl_set_for_stage_resource(stage);
        *out_binding = sdl_read_storage_buffer_binding(refl, stage, b);
      }
      return true;
    }
  }
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    const ShaderStorageTexture *t = &refl->storage_texs[i];
    if (t->stage == stage && strcmp(t->name, name) == 0) {
      if (stage == SGL_STAGE_COMPUTE && !t->readonly) {
        *out_set = 1;
        *out_binding = t->slot;
      } else {
        *out_set = sdl_set_for_stage_resource(stage);
        *out_binding = sdl_read_storage_texture_binding(refl, stage, t);
      }
      return true;
    }
  }
  return false;
}

void patch_spirv_bindings_from_reflection(void *spv, size_t size_bytes,
                                          SpvStage spv_stage,
                                          const ShaderReflection *refl) {
  if (!spv || size_bytes < 20 || !refl)
    return;
  uint32_t *words = (uint32_t *)spv;
  size_t nwords = size_bytes / 4;
  if (words[0] != 0x07230203u)
    return;

  constexpr uint32_t kOpName = 5;
  constexpr uint32_t kOpDecorate = 71;
  constexpr uint32_t kDecBinding = 33;
  constexpr uint32_t kDecDescriptorSet = 34;

  struct SpvNamedId {
    uint32_t id;
    std::string name;
  };
  std::vector<SpvNamedId> names;

  size_t i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpName && wc >= 3) {
      uint32_t id = words[i + 1];
      const char *name = (const char *)&words[i + 2];
      names.push_back({id, name ? name : ""});
    }
    i += wc;
  }

  auto name_of = [&](uint32_t id) -> const char * {
    for (const auto &n : names) {
      if (n.id == id)
        return n.name.c_str();
    }
    return nullptr;
  };

  SglShaderStage stage = to_sgl_stage(spv_stage);
  i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpDecorate && wc >= 4) {
      uint32_t id = words[i + 1];
      uint32_t deco = words[i + 2];
      const char *name = name_of(id);
      int set = -1;
      int binding = -1;
      if (reflected_binding_for_name(refl, stage, name, &set, &binding)) {
        if (deco == kDecDescriptorSet && set >= 0) {
          words[i + 3] = (uint32_t)set;
        } else if (deco == kDecBinding && binding >= 0) {
          words[i + 3] = (uint32_t)binding;
        }
      }
    }
    i += wc;
  }
}

// After patch_spirv_bindings_from_reflection: every descriptor variable must
// sit where the reflection (and so the backend's layout) puts it. One that
// was left at Slang's own numbering reads a descriptor the pipeline layout
// doesn't have, which is undefined behavior on the GPU instead of an error.
bool check_spirv_layout(const void *spv, size_t size_bytes, SpvStage spv_stage,
                        const ShaderReflection *refl, char *err, size_t errsz) {
  if (!spv || size_bytes < 20 || !refl)
    return true;
  const uint32_t *words = (const uint32_t *)spv;
  size_t nwords = size_bytes / 4;
  if (words[0] != 0x07230203u)
    return true;

  constexpr uint32_t kOpName = 5;
  constexpr uint32_t kOpVariable = 59;
  constexpr uint32_t kOpDecorate = 71;
  constexpr uint32_t kDecBinding = 33;
  constexpr uint32_t kDecDescriptorSet = 34;
  constexpr uint32_t kStorageUniformConstant = 0;
  constexpr uint32_t kStorageUniform = 2;
  constexpr uint32_t kStorageStorageBuffer = 12;

  struct Var {
    uint32_t id;
    std::string name;
    int set = -1;
    int binding = -1;
  };
  std::vector<Var> vars;
  auto find = [&](uint32_t id) -> Var * {
    for (auto &v : vars)
      if (v.id == id)
        return &v;
    return nullptr;
  };
  for (int pass = 0; pass < 2; ++pass) {
    size_t i = 5;
    while (i < nwords) {
      uint32_t wc = words[i] >> 16;
      uint32_t op = words[i] & 0xffff;
      if (wc == 0 || i + wc > nwords)
        break;
      if (pass == 0 && op == kOpVariable && wc >= 4 &&
          (words[i + 3] == kStorageUniformConstant ||
           words[i + 3] == kStorageUniform ||
           words[i + 3] == kStorageStorageBuffer)) {
        vars.push_back({words[i + 2], ""});
      } else if (pass == 1 && op == kOpName && wc >= 3) {
        if (Var *v = find(words[i + 1]))
          v->name.assign((const char *)&words[i + 2],
                         strnlen((const char *)&words[i + 2], (wc - 2) * 4));
      } else if (pass == 1 && op == kOpDecorate && wc >= 4) {
        Var *v = find(words[i + 1]);
        if (v && words[i + 2] == kDecDescriptorSet)
          v->set = (int)words[i + 3];
        else if (v && words[i + 2] == kDecBinding)
          v->binding = (int)words[i + 3];
      }
      i += wc;
    }
  }

  SglShaderStage stage = to_sgl_stage(spv_stage);
  for (const Var &v : vars) {
    if (v.set < 0 && v.binding < 0)
      continue;
    int set = -1, binding = -1;
    if (reflected_binding_for_name(refl, stage, v.name.c_str(), &set,
                                   &binding) &&
        set == v.set && binding == v.binding && binding >= 0)
      continue;
    if (err && errsz)
      snprintf(err, errsz,
               "descriptor layout: %s shader variable '%s' sits at set %d "
               "binding %d, outside the layout lub builds for this shader. "
               "lub binds textures declared with LUB_TEXTURE2D(name), "
               "StructuredBuffer / RWStructuredBuffer / RWTexture2D, and "
               "cbuffer blocks; a global outside a cbuffer is not bound",
               stage_label(stage), v.name.empty() ? "?" : v.name.c_str(), v.set,
               v.binding);
    return false;
  }
  return true;
}

void patch_spirv_storage_image_formats(void *spv, size_t size_bytes,
                                       SpvStage spv_stage,
                                       const ShaderReflection *refl) {
  if (!spv || size_bytes < 20 || !refl)
    return;
  uint32_t *words = (uint32_t *)spv;
  size_t nwords = size_bytes / 4;
  if (words[0] != 0x07230203u)
    return;

  constexpr uint32_t kOpCapability = 17;
  constexpr uint32_t kOpTypeImage = 25;
  constexpr uint32_t kOpTypePointer = 32;
  constexpr uint32_t kOpVariable = 59;
  constexpr uint32_t kOpName = 5;
  constexpr uint32_t kCapabilityShader = 1;
  constexpr uint32_t kCapabilityStorageImageReadWithoutFormat = 55;
  constexpr uint32_t kCapabilityStorageImageWriteWithoutFormat = 56;
  constexpr uint32_t kStorageUniformConstant = 0;
  constexpr uint32_t kImageFormatUnknown = 0;
  constexpr uint32_t kSampledStorageImage = 2;

  struct NamedId {
    uint32_t id;
    std::string name;
  };
  struct PointerType {
    uint32_t id;
    uint32_t pointee;
  };
  struct Variable {
    uint32_t id;
    uint32_t type_id;
  };
  std::vector<NamedId> names;
  std::vector<PointerType> pointers;
  std::vector<Variable> vars;
  std::vector<std::pair<uint32_t, uint32_t>> image_type_to_format;

  size_t i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpName && wc >= 3) {
      uint32_t id = words[i + 1];
      const char *name = (const char *)&words[i + 2];
      names.push_back({id, name ? name : ""});
    } else if (op == kOpTypePointer && wc >= 4) {
      pointers.push_back({words[i + 1], words[i + 3]});
    } else if (op == kOpVariable && wc >= 4 &&
               words[i + 3] == kStorageUniformConstant) {
      vars.push_back({words[i + 2], words[i + 1]});
    }
    i += wc;
  }

  auto name_of = [&](uint32_t id) -> const char * {
    for (const auto &n : names) {
      if (n.id == id)
        return n.name.c_str();
    }
    return nullptr;
  };
  auto pointee_of = [&](uint32_t ptr_type) -> uint32_t {
    for (const auto &p : pointers) {
      if (p.id == ptr_type)
        return p.pointee;
    }
    return 0;
  };
  auto image_format_for_name = [&](const char *name, uint32_t *out) -> bool {
    if (!name || !out)
      return false;
    SglShaderStage stage = to_sgl_stage(spv_stage);
    for (int k = 0; k < refl->storage_tex_count; ++k) {
      const ShaderStorageTexture *st = &refl->storage_texs[k];
      if (st->stage == stage && strcmp(st->name, name) == 0) {
        *out = sgl_to_spv_image_format(st->access_format);
        return true;
      }
    }
    return false;
  };
  auto set_image_type_format = [&](uint32_t image_type, uint32_t fmt) {
    if (image_type == 0 || fmt == kImageFormatUnknown)
      return;
    for (auto &p : image_type_to_format) {
      if (p.first == image_type) {
        if (p.second == kImageFormatUnknown)
          p.second = fmt;
        return;
      }
    }
    image_type_to_format.push_back({image_type, fmt});
  };
  auto format_for_image_type = [&](uint32_t image_type) -> uint32_t {
    for (const auto &p : image_type_to_format) {
      if (p.first == image_type)
        return p.second;
    }
    return sgl_to_spv_image_format(SGL_PF_RGBA16F);
  };

  for (const auto &v : vars) {
    uint32_t fmt = kImageFormatUnknown;
    if (image_format_for_name(name_of(v.id), &fmt))
      set_image_type_format(pointee_of(v.type_id), fmt);
  }

  i = 5;
  while (i < nwords) {
    uint32_t hdr = words[i];
    uint32_t wc = hdr >> 16;
    uint32_t op = hdr & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpCapability && wc >= 2 &&
        (words[i + 1] == kCapabilityStorageImageReadWithoutFormat ||
         words[i + 1] == kCapabilityStorageImageWriteWithoutFormat)) {
      words[i + 1] = kCapabilityShader;
    } else if (op == kOpTypeImage && wc >= 9 &&
               words[i + 7] == kSampledStorageImage &&
               words[i + 8] == kImageFormatUnknown) {
      words[i + 8] = format_for_image_type(words[i + 1]);
    }
    i += wc;
  }
}

// Metal: make StructuredBuffer elements pack tightly. Slang emits `float3`
// members, which MSL aligns and sizes to 16 bytes; every other target reads
// the same flat float list with a 12-byte float3 (check_tight_std430 has
// already required the struct to be tight). The packed vector types fix the
// layout but don't work with matrix operators, so the shader keeps computing
// on the struct Slang emitted and only the buffer memory changes type: every
// struct reachable from a `device*` buffer parameter gets a `<name>_packed`
// twin with packed 3-component vectors that converts to and from the
// original, and the buffer pointers are retargeted to the twin.
static void msl_pack_buffer_structs(std::string &msl) {
  static const std::regex vec3(R"(\b(float|int|uint)3\b)");
  static const std::regex vec3_ptr(
      R"(\b(float|int|uint)3(\s+(?:const\s+)?device\s*\*))");
  static const std::regex buf_ptr(R"(\b(\w+)\s+(?:const\s+)?device\s*\*)");
  static const std::regex struct_def(R"(\bstruct\s+(\w+)\s*\{([^{}]*)\}\s*;)");
  static const std::regex ident(R"(\b[A-Za-z_]\w*\b)");
  static const std::regex member(R"(^\s*(.*\S)\s+([A-Za-z_]\w*)\s*$)");
  static const std::regex array_type(R"(^array<.*,\s*int\((\d+)\)\s*>$)");

  // StructuredBuffer<float3> and friends: the element itself is the vector.
  msl = std::regex_replace(msl, vec3_ptr, "packed_$013$2");

  struct Def {
    std::string name, body;
    size_t end;
  };
  std::vector<Def> defs;
  for (std::sregex_iterator it(msl.begin(), msl.end(), struct_def), end;
       it != end; ++it)
    defs.push_back({(*it)[1].str(), (*it)[2].str(),
                    (size_t)(it->position(0) + it->length(0))});

  std::set<std::string> packed;
  std::vector<std::string> work;
  for (std::sregex_iterator it(msl.begin(), msl.end(), buf_ptr), end; it != end;
       ++it)
    work.push_back((*it)[1].str());
  while (!work.empty()) {
    std::string name = work.back();
    work.pop_back();
    for (const Def &d : defs) {
      if (d.name != name || !packed.insert(name).second)
        continue;
      for (std::sregex_iterator it(d.body.begin(), d.body.end(), ident), end;
           it != end; ++it)
        work.push_back(it->str());
    }
  }
  if (packed.empty())
    return;

  auto packed_type = [&](const std::string &type) {
    std::string vectors = std::regex_replace(type, vec3, "packed_$013");
    std::string out;
    size_t pos = 0;
    for (std::sregex_iterator it(vectors.begin(), vectors.end(), ident), end;
         it != end; ++it) {
      out.append(vectors, pos, (size_t)it->position(0) - pos);
      out += it->str();
      if (packed.count(it->str()))
        out += "_packed";
      pos = (size_t)(it->position(0) + it->length(0));
    }
    out.append(vectors, pos, std::string::npos);
    return out;
  };

  // Back to front so earlier offsets stay valid. A twin sits right after its
  // original, which is after any struct it nests.
  for (size_t i = defs.size(); i-- > 0;) {
    const Def &d = defs[i];
    if (!packed.count(d.name))
      continue;
    std::string fields, load, store;
    size_t pos = 0;
    while (pos < d.body.size()) {
      size_t semi = d.body.find(';', pos);
      if (semi == std::string::npos)
        break;
      std::string decl = d.body.substr(pos, semi - pos);
      pos = semi + 1;
      std::smatch m;
      if (!std::regex_match(decl, m, member))
        continue;
      std::string type = m[1].str(), name = m[2].str();
      std::string twin = packed_type(type);
      fields += "    " + twin + " " + name + ";\n";
      std::smatch am;
      if (twin != type && std::regex_match(type, am, array_type)) {
        std::string loop =
            "        for (int i = 0; i < " + am[1].str() + "; ++i) ";
        load += loop + "r." + name + "[i] = " + name + "[i];\n";
        store += loop + name + "[i] = v." + name + "[i];\n";
      } else {
        load += "        r." + name + " = " + name + ";\n";
        store += "        " + name + " = v." + name + ";\n";
      }
    }
    msl.insert(d.end,
               "\nstruct " + d.name + "_packed\n{\n" + fields +
                   "    operator " + d.name + "() const device\n    {\n" +
                   "        " + d.name + " r;\n" + load +
                   "        return r;\n    }\n    void operator=(" + d.name +
                   " v) device\n    {\n" + store + "    }\n};\n");
  }
  for (const std::string &name : packed)
    msl = std::regex_replace(
        msl, std::regex("\\b" + name + R"((\s+(?:const\s+)?device\s*\*))"),
        name + "_packed$1");
}

// Metal: turn Slang's MSL blob into the NUL-terminated source the backend
// hands to newLibraryWithSource.
static bool finish_msl_blob(ShaderBlob *blob) {
  std::string msl((const char *)blob->spirv, blob->bytes);
  msl_pack_buffer_structs(msl);
  char *text = (char *)malloc(msl.size() + 1);
  if (!text)
    return false;
  memcpy(text, msl.c_str(), msl.size() + 1);
  free(blob->spirv);
  blob->spirv = (uint32_t *)text;
  blob->bytes = msl.size();
  return true;
}

static void remap_stage_for_sdlgpu(ShaderReflection *stage) {
  for (int i = 0; i < stage->ub_count; ++i) {
    stage->ubs[i].slot = i;
  }
  for (int i = 0; i < stage->tex_count; ++i) {
    stage->texs[i].img_slot = i;
    stage->texs[i].smp_slot = i;
  }

  int ro_stex = 0;
  int rw_stex = 0;
  for (int i = 0; i < stage->storage_tex_count; ++i) {
    stage->storage_texs[i].slot =
        stage->storage_texs[i].readonly ? ro_stex++ : rw_stex++;
  }

  int ro_sbuf = 0;
  int rw_sbuf = 0;
  for (int i = 0; i < stage->storage_buf_count; ++i) {
    stage->storage_bufs[i].slot =
        stage->storage_bufs[i].readonly ? ro_sbuf++ : rw_sbuf++;
  }
}

static void merge_stage_reflection(ShaderReflection *dst,
                                   const ShaderReflection *src,
                                   ShaderTargetBackend target) {
  ShaderReflection stage = *src;
  if (target == SHADER_TARGET_SDLGPU) {
    remap_stage_for_sdlgpu(&stage);
  }
  // D3D12: no remap. The slots are Slang's HLSL register indices and the
  // DXIL blob can't be re-numbered after the fact; the backend builds its
  // root signature from these values instead.
  // Metal: no remap either. The slots are the per-stage [[buffer]] /
  // [[texture]] / [[sampler]] indices in the MSL, and the backend binds to
  // them directly.

  for (int i = 0; i < stage.ub_count && dst->ub_count < SGL_MAX_UNIFORM_BLOCKS;
       ++i) {
    dst->ubs[dst->ub_count++] = stage.ubs[i];
  }
  for (int i = 0; i < stage.tex_count && dst->tex_count < SGL_MAX_TEXTURES;
       ++i) {
    dst->texs[dst->tex_count++] = stage.texs[i];
  }
  for (int i = 0; i < stage.storage_buf_count &&
                  dst->storage_buf_count < SGL_MAX_STORAGE_BUFS;
       ++i) {
    dst->storage_bufs[dst->storage_buf_count++] = stage.storage_bufs[i];
  }
  for (int i = 0; i < stage.storage_tex_count &&
                  dst->storage_tex_count < SGL_MAX_STORAGE_TEXTURES;
       ++i) {
    dst->storage_texs[dst->storage_tex_count++] = stage.storage_texs[i];
  }
  if (stage.is_compute) {
    dst->is_compute = true;
    dst->workgroup[0] = stage.workgroup[0];
    dst->workgroup[1] = stage.workgroup[1];
    dst->workgroup[2] = stage.workgroup[2];
  }
}

// D3D12 graphics path: VS+FS must be linked into ONE slang program. DXIL
// matches varyings between stages by hardware register (not by location as
// SPIR-V/Vulkan does), and separately-compiled programs pack their varying
// signatures independently — e.g. VS emits SV_Position at o0 pushing COLOR
// to o1 while the FS expects COLOR at v0. Linking both entry points lets
// Slang lay out one consistent inter-stage signature. Side effect: b/t/s/u
// registers become program-unique across stages, which the d3d12 backend
// relies on (single root-signature tables with SHADER_VISIBILITY_ALL).
bool compile_d3d12_graphics(const char *vs_src, const char *fs_src,
                            ShaderBlob *out_vs, ShaderBlob *out_fs,
                            ShaderReflection *out_refl, char *err_buf,
                            size_t err_buf_size) {
  TargetDesc slang_target = {};
  configure_target(&slang_target, SHADER_TARGET_D3D12);
  SessionDesc sd = {};
  sd.targets = &slang_target;
  sd.targetCount = 1;
  sd.defaultMatrixLayoutMode = SLANG_MATRIX_LAYOUT_ROW_MAJOR;
  ComPtr<ISession> session;
  if (SLANG_FAILED(g_slang.g->createSession(sd, session.writeRef()))) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createSession failed");
    return false;
  }

  const char *prelude = prelude_for_target(SHADER_TARGET_D3D12);
  auto load = [&](const char *src, const char *mod_name, const char *entry,
                  ComPtr<IModule> &mod, ComPtr<IEntryPoint> &ep) -> bool {
    std::string full(prelude);
    full += src;
    ComPtr<IBlob> diag;
    IModule *raw = session->loadModuleFromSourceString(
        mod_name, (std::string(mod_name) + ".slang").c_str(), full.c_str(),
        diag.writeRef());
    if (!raw) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }
    mod = ComPtr<IModule>(raw);
    if (SLANG_FAILED(mod->findEntryPointByName(entry, ep.writeRef())) || !ep) {
      if (err_buf && err_buf_size)
        snprintf(err_buf, err_buf_size, "%s entry point not found", entry);
      return false;
    }
    return true;
  };
  ComPtr<IModule> vs_mod, fs_mod;
  ComPtr<IEntryPoint> vs_ep, fs_ep;
  if (!load(vs_src, "user_vs", "vs_main", vs_mod, vs_ep))
    return false;
  if (!load(fs_src, "user_fs", "fs_main", fs_mod, fs_ep))
    return false;

  IComponentType *components[] = {vs_mod.get(), vs_ep.get(), fs_mod.get(),
                                  fs_ep.get()};
  ComPtr<IBlob> diag;
  ComPtr<IComponentType> composite;
  if (SLANG_FAILED(session->createCompositeComponentType(
          components, 4, composite.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  ComPtr<IComponentType> linked;
  if (SLANG_FAILED(composite->link(linked.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  // Entry point indices follow composition order: 0 = vs, 1 = fs.
  ComPtr<IBlob> vs_code, fs_code;
  if (SLANG_FAILED(linked->getEntryPointCode(0, 0, vs_code.writeRef(),
                                             diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  if (SLANG_FAILED(linked->getEntryPointCode(1, 0, fs_code.writeRef(),
                                             diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  ProgramLayout *layout = linked->getLayout(0, diag.writeRef());
  if (!layout) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  // Stage attribution. The linked layout's registers are what the DXIL uses,
  // but it doesn't say which stage consumes a parameter — recover that by
  // matching names against each module's own parameter list.
  auto collect_names = [&](IModule *mod, std::vector<std::string> *names) {
    ComPtr<IComponentType> ml;
    ComPtr<IBlob> d2;
    if (SLANG_FAILED(mod->link(ml.writeRef(), d2.writeRef())) || !ml)
      return;
    ProgramLayout *l = ml->getLayout(0, d2.writeRef());
    if (!l)
      return;
    unsigned n = l->getParameterCount();
    for (unsigned i = 0; i < n; ++i) {
      VariableLayoutReflection *p = l->getParameterByIndex(i);
      if (p && p->getName())
        names->push_back(p->getName());
    }
  };
  std::vector<std::string> vs_names, fs_names;
  collect_names(vs_mod.get(), &vs_names);
  collect_names(fs_mod.get(), &fs_names);
  auto has = [](const std::vector<std::string> &v, const char *n) {
    for (const auto &s : v)
      if (s == n)
        return true;
    return false;
  };
  unsigned gpc = layout->getParameterCount();
  for (int pass = 0; pass < 2; ++pass) {
    SglShaderStage stage = pass == 0 ? SGL_STAGE_VERTEX : SGL_STAGE_FRAGMENT;
    const std::vector<std::string> &names = pass == 0 ? vs_names : fs_names;
    std::vector<ReflSampler> samplers;
    for (unsigned i = 0; i < gpc; ++i) {
      VariableLayoutReflection *p = layout->getParameterByIndex(i);
      if (!p || !p->getName())
        continue;
      if (!has(names, p->getName()))
        continue;
      if (!fill_global_param(p, out_refl, stage, SHADER_TARGET_D3D12, &samplers,
                             err_buf, err_buf_size))
        return false;
    }
    if (!pair_samplers(out_refl, stage, samplers, err_buf, err_buf_size))
      return false;
  }

  auto copy_code = [&](IBlob *code, ShaderBlob *out) -> bool {
    size_t size = code->getBufferSize();
    out->spirv = (uint32_t *)malloc(size);
    if (!out->spirv)
      return false;
    memcpy(out->spirv, code->getBufferPointer(), size);
    out->bytes = size;
    return true;
  };
  if (!copy_code(vs_code.get(), out_vs) || !copy_code(fs_code.get(), out_fs)) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (d3d12 blobs)");
    return false;
  }
  return true;
}

} // anonymous namespace

static bool compile_graphics(const char *vs_src, const char *fs_src,
                             ShaderTargetBackend target, ShaderBlob *out_vs,
                             ShaderBlob *out_fs, ShaderReflection *out_refl,
                             char *err_buf, size_t err_buf_size) {
  if (!ensure_global_session()) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createGlobalSession failed");
    return false;
  }

  if (target == SHADER_TARGET_D3D12) {
    return compile_d3d12_graphics(vs_src, fs_src, out_vs, out_fs, out_refl,
                                  err_buf, err_buf_size);
  }

  TargetDesc slang_target = {};
  configure_target(&slang_target, target);

  SessionDesc sd = {};
  sd.targets = &slang_target;
  sd.targetCount = 1;
  sd.defaultMatrixLayoutMode = SLANG_MATRIX_LAYOUT_ROW_MAJOR;

  ComPtr<ISession> session;
  if (SLANG_FAILED(g_slang.g->createSession(sd, session.writeRef()))) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createSession failed");
    return false;
  }

  const char *prelude = prelude_for_target(target);

  auto compile_stage = [&](const char *src, const char *entry,
                           const char *module_name, SglShaderStage sgl_stage,
                           SpvStage spv_stage, ShaderBlob *out_blob,
                           ShaderReflection *stage_refl) -> bool {
    memset(stage_refl, 0, sizeof(*stage_refl));
    std::string source;
    source.reserve(strlen(prelude) + strlen(src) + 1);
    source.append(prelude);
    source.append(src);

    ComPtr<IBlob> diag;
    IModule *modRaw = session->loadModuleFromSourceString(
        module_name, (std::string(module_name) + ".slang").c_str(),
        source.c_str(), diag.writeRef());
    if (!modRaw) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }
    ComPtr<IModule> module(modRaw);

    ComPtr<IEntryPoint> ep;
    if (SLANG_FAILED(module->findEntryPointByName(entry, ep.writeRef())) ||
        !ep) {
      if (err_buf && err_buf_size)
        snprintf(err_buf, err_buf_size, "%s entry point not found", entry);
      return false;
    }

    IComponentType *components[] = {module.get(), ep.get()};
    ComPtr<IComponentType> composite;
    if (SLANG_FAILED(session->createCompositeComponentType(
            components, 2, composite.writeRef(), diag.writeRef()))) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }
    ComPtr<IComponentType> linked;
    if (SLANG_FAILED(composite->link(linked.writeRef(), diag.writeRef()))) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }

    ComPtr<IBlob> code;
    if (SLANG_FAILED(linked->getEntryPointCode(0, 0, code.writeRef(),
                                               diag.writeRef()))) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }

    ProgramLayout *programLayout = linked->getLayout(0, diag.writeRef());
    if (!programLayout) {
      copy_diag(diag.get(), err_buf, err_buf_size);
      return false;
    }

    if (!fill_global_reflection(programLayout, stage_refl, sgl_stage, target,
                                err_buf, err_buf_size))
      return false;

    size_t size = code->getBufferSize();
    out_blob->spirv = (uint32_t *)malloc(size);
    if (!out_blob->spirv) {
      if (err_buf && err_buf_size)
        snprintf(err_buf, err_buf_size, "OOM (%s blob)", entry);
      return false;
    }
    memcpy(out_blob->spirv, code->getBufferPointer(), size);
    out_blob->bytes = size;
    (void)spv_stage;
    return true;
  };

  ShaderReflection vs_refl;
  ShaderReflection fs_refl;
  memset(&vs_refl, 0, sizeof(vs_refl));
  memset(&fs_refl, 0, sizeof(fs_refl));
  if (!compile_stage(vs_src, "vs_main", "user_vs", SGL_STAGE_VERTEX,
                     SpvStage::Vertex, out_vs, &vs_refl)) {
    shader_blob_free(out_vs);
    return false;
  }
  if (!compile_stage(fs_src, "fs_main", "user_fs", SGL_STAGE_FRAGMENT,
                     SpvStage::Fragment, out_fs, &fs_refl)) {
    shader_blob_free(out_vs);
    shader_blob_free(out_fs);
    return false;
  }

  merge_stage_reflection(out_refl, &vs_refl, target);
  merge_stage_reflection(out_refl, &fs_refl, target);

  if (target == SHADER_TARGET_SDLGPU) {
    patch_spirv_bindings_from_reflection(out_vs->spirv, out_vs->bytes,
                                         SpvStage::Vertex, out_refl);
    patch_spirv_bindings_from_reflection(out_fs->spirv, out_fs->bytes,
                                         SpvStage::Fragment, out_refl);
    patch_spirv_storage_image_formats(out_vs->spirv, out_vs->bytes,
                                      SpvStage::Vertex, out_refl);
    patch_spirv_storage_image_formats(out_fs->spirv, out_fs->bytes,
                                      SpvStage::Fragment, out_refl);
    if (!check_spirv_layout(out_vs->spirv, out_vs->bytes, SpvStage::Vertex,
                            out_refl, err_buf, err_buf_size) ||
        !check_spirv_layout(out_fs->spirv, out_fs->bytes, SpvStage::Fragment,
                            out_refl, err_buf, err_buf_size)) {
      shader_blob_free(out_vs);
      shader_blob_free(out_fs);
      return false;
    }
  }
  if (target == SHADER_TARGET_METAL &&
      (!finish_msl_blob(out_vs) || !finish_msl_blob(out_fs))) {
    shader_blob_free(out_vs);
    shader_blob_free(out_fs);
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (msl blobs)");
    return false;
  }
  return true;
}

static bool compile_compute(const char *cs_src, ShaderTargetBackend target,
                            ShaderBlob *out_cs, ShaderReflection *out_refl,
                            char *err_buf, size_t err_buf_size) {
  if (!ensure_global_session()) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createGlobalSession failed");
    return false;
  }

  TargetDesc slang_target = {};
  configure_target(&slang_target, target);

  SessionDesc sd = {};
  sd.targets = &slang_target;
  sd.targetCount = 1;
  sd.defaultMatrixLayoutMode = SLANG_MATRIX_LAYOUT_ROW_MAJOR;

  ComPtr<ISession> session;
  if (SLANG_FAILED(g_slang.g->createSession(sd, session.writeRef()))) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createSession failed");
    return false;
  }

  const char *prelude = prelude_for_target(target);
  std::string cs_with_prelude;
  cs_with_prelude.reserve(strlen(prelude) + strlen(cs_src) + 1);
  cs_with_prelude.append(prelude);
  cs_with_prelude.append(cs_src);

  ComPtr<IBlob> diag;
  IModule *modRaw = session->loadModuleFromSourceString(
      "user_cs", "user_cs.slang", cs_with_prelude.c_str(), diag.writeRef());
  if (!modRaw) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  ComPtr<IModule> module(modRaw);

  ComPtr<IEntryPoint> csEp;
  if (SLANG_FAILED(module->findEntryPointByName("cs_main", csEp.writeRef())) ||
      !csEp) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "cs_main entry point not found");
    return false;
  }

  IComponentType *components[] = {module.get(), csEp.get()};
  ComPtr<IComponentType> composite;
  if (SLANG_FAILED(session->createCompositeComponentType(
          components, 2, composite.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  ComPtr<IComponentType> linked;
  if (SLANG_FAILED(composite->link(linked.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  ComPtr<IBlob> csBlob;
  if (SLANG_FAILED(linked->getEntryPointCode(0, 0, csBlob.writeRef(),
                                             diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  ProgramLayout *programLayout = linked->getLayout(0, diag.writeRef());
  if (!programLayout) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  out_refl->is_compute = true;
  out_refl->workgroup[0] = out_refl->workgroup[1] = out_refl->workgroup[2] = 1;
  SlangUInt epc = programLayout->getEntryPointCount();
  for (SlangUInt i = 0; i < epc; ++i) {
    EntryPointReflection *ep = programLayout->getEntryPointByIndex(i);
    if (!ep || ep->getStage() != SLANG_STAGE_COMPUTE)
      continue;
    SlangUInt sizes[3] = {1, 1, 1};
    ep->getComputeThreadGroupSize(3, sizes);
    out_refl->workgroup[0] = (int)sizes[0];
    out_refl->workgroup[1] = (int)sizes[1];
    out_refl->workgroup[2] = (int)sizes[2];
    break;
  }
  if (!fill_global_reflection(programLayout, out_refl, SGL_STAGE_COMPUTE,
                              target, err_buf, err_buf_size))
    return false;
  if (target == SHADER_TARGET_SDLGPU)
    remap_stage_for_sdlgpu(out_refl);

  size_t cs_size = csBlob->getBufferSize();
  out_cs->spirv = (uint32_t *)malloc(cs_size);
  if (!out_cs->spirv) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (cs blob)");
    return false;
  }
  memcpy(out_cs->spirv, csBlob->getBufferPointer(), cs_size);
  out_cs->bytes = cs_size;

  if (target == SHADER_TARGET_SDLGPU) {
    patch_spirv_bindings_from_reflection(out_cs->spirv, out_cs->bytes,
                                         SpvStage::Compute, out_refl);
    patch_spirv_storage_image_formats(out_cs->spirv, out_cs->bytes,
                                      SpvStage::Compute, out_refl);
    if (!check_spirv_layout(out_cs->spirv, out_cs->bytes, SpvStage::Compute,
                            out_refl, err_buf, err_buf_size)) {
      shader_blob_free(out_cs);
      return false;
    }
  }
  if (target == SHADER_TARGET_METAL && !finish_msl_blob(out_cs)) {
    shader_blob_free(out_cs);
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (msl blob)");
    return false;
  }
  return true;
}

#elif defined(__EMSCRIPTEN__)

// -------------------------------------------------------------------------
// Emscripten Slang bridge.
//
// We delegate Slang compilation to the JS side via EM_ASYNC_JS. The JS
// glue (`window.slangCompile`, defined in web/playground/slang-bridge.ts)
// loads `@shader-slang/slang-wasm`, runs Slang in-page, and returns
// `{wgsl, reflectJson}` (or `{error}`).
//
// File layout (this block):
//   1. EM_ASYNC_JS shim `lub_slang_compile_js` — single async call point.
//   2. `reflect_from_slang_json` — populate ShaderReflection from Slang's
//      reflection JSON.
//   3. `shader_compile` / `shader_compile_compute` — drive (1) twice/once
//      and (2) per blob, returning WGSL bytes into ShaderBlob.spirv.

#include "../third_party/nlohmann/json.hpp"
#include <emscripten.h>

// Stage codes passed across the JS bridge to window.slangCompile.
// Must match what playground/slang-bridge.ts expects.
enum SlangBridgeStage {
  SLANG_BRIDGE_STAGE_VS = 0,
  SLANG_BRIDGE_STAGE_FS = 1,
  SLANG_BRIDGE_STAGE_CS = 2,
};

// JS bridge into window.slangCompile().
//
// Contract:
//   * `src`   — Slang source string (UTF-8).
//   * `entry` — entry-point name (e.g. "vs_main", "fs_main", "cs_main").
//   * `stage` — SglShaderStage (0=vertex, 1=fragment, 2=compute). JS side
//                passes this straight to slang-wasm's stage enum.
//
// Return value (malloc'd UTF-8 char*, caller frees with free()):
//   * Success: `wgsl + '\x01' + reflectJson` — leading byte is the first byte
//     of valid WGSL source (never 0x02). The 0x01 separator cannot appear in
//     valid WGSL or JSON.
//   * Failure (diagnostic available): `'\x02' + errorString`. The leading 0x02
//     byte signals that the rest is a human-readable Slang diagnostic to be
//     surfaced in `err_buf`.
//   * NULL: only for genuinely unrecoverable cases (malloc failure inside the
//     JS shim). Everything else — including "slang-wasm not loaded yet" — uses
//     the 0x02-prefixed error form so the diagnostic reaches the user.
// EM_ASYNC_JS body below is JavaScript, not C++. clang-format mangles JS
// operators (=== becomes "== =", !== becomes "!= =") and breaks the generated
// lub.js WASM glue, so disable formatting for this region. The native build
// drops this block via #ifdef __EMSCRIPTEN__, so a reformat only breaks web
// and native build/golden won't catch it — keep the clang-format off guard.
// clang-format off
EM_ASYNC_JS(
    char *, lub_slang_compile_js,
    (const char *src, const char *entry, int stage), {
      const srcStr = UTF8ToString(src);
      const entryStr = UTF8ToString(entry);
      const packError = (msg) => {
        const errMsg = '\x02' + msg;
        const len = lengthBytesUTF8(errMsg) + 1;
        const ptr = _malloc(len);
        if (!ptr)
          return 0;
        stringToUTF8(errMsg, ptr, len);
        return ptr;
      };
      if (typeof window === 'undefined' ||
          typeof window.slangCompile !== 'function') {
        console.error('[lub] window.slangCompile not exposed by the host; ' +
                      'slang-wasm bridge not loaded. entry=' + entryStr);
        return packError(
            'slang-wasm bridge not loaded (window.slangCompile undefined)');
      }
      try {
        const result = await window.slangCompile(srcStr, entryStr, stage);
        if (!result || result.error) {
          const msg = (result && result.error)
                          ? result.error
                          : 'slang compile returned no result';
          console.error('[lub] slang compile error:', msg);
          return packError(msg);
        }
        // Pack {wgsl, reflectJson} into a single \x01-separated UTF-8 string.
        const blob = result.wgsl + '\x01' + (result.reflectJson || '{}');
        const len = lengthBytesUTF8(blob) + 1;
        const ptr = _malloc(len);
        if (!ptr)
          return 0;
        stringToUTF8(blob, ptr, len);
        return ptr;
      } catch (e) {
        const msg = (e && e.message) ? e.message : String(e);
        console.error('[lub] slangCompile threw:', msg);
        return packError(msg);
      }
    });
// clang-format on

namespace {

using json = nlohmann::json;

void copy_name_capped(char *dst, size_t cap, const std::string &src) {
  if (cap == 0)
    return;
  size_t n = src.size();
  if (n >= cap)
    n = cap - 1;
  memcpy(dst, src.data(), n);
  dst[n] = '\0';
}

// Split "wgsl\x01reflectJson" on the first 0x01 byte. Returns false if no sep.
bool split_blob(const char *blob, std::string &out_wgsl,
                std::string &out_refl_json) {
  if (!blob)
    return false;
  const char *sep = strchr(blob, '\x01');
  if (!sep)
    return false;
  out_wgsl.assign(blob, (size_t)(sep - blob));
  out_refl_json.assign(sep + 1);
  return true;
}

// Cross-stage dedup helpers. shader_compile() merges VS and FS reflection
// JSON into one ShaderReflection by calling reflect_from_slang_json twice.
// If both stages declare the same UB/texture/storage-buffer (very common —
// e.g. a UB at slot 0 used by both vertex and fragment), the second call
// would otherwise append a duplicate entry. We guard each append with a
// slot-existence check.
bool ub_slot_exists(const ShaderReflection *refl, SglShaderStage stage,
                    int slot) {
  for (int i = 0; i < refl->ub_count; ++i) {
    if (refl->ubs[i].stage == stage && refl->ubs[i].slot == slot)
      return true;
  }
  return false;
}
bool tex_slot_exists(const ShaderReflection *refl, SglShaderStage stage,
                     int img_slot) {
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].stage == stage && refl->texs[i].img_slot == img_slot)
      return true;
  }
  return false;
}
// Used by the storage-buffer dedup path in reflect_from_slang_json.
bool sbuf_slot_exists(const ShaderReflection *refl, SglShaderStage stage,
                      int slot) {
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (refl->storage_bufs[i].stage == stage &&
        refl->storage_bufs[i].slot == slot)
      return true;
  }
  return false;
}
bool stex_slot_exists(const ShaderReflection *refl, SglShaderStage stage,
                      int slot) {
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (refl->storage_texs[i].stage == stage &&
        refl->storage_texs[i].slot == slot)
      return true;
  }
  return false;
}

// ---------------------------------------------------------------------------
// Reflection schema reference (Slang WASM, release v2026.8.1+):
//
// Top-level object from ProgramLayout::toJsonObject():
// {
//   "parameters": [           // global parameters (UBs, textures, samplers,
//   storage)
//     {
//       "name": "...",
//       "binding": { "kind": "descriptorTableSlot", "index": N },
//       "type": {
//         "kind": "constantBuffer" | "resource" | "samplerState" | ...,
//         // ConstantBuffer<T>:
//         "elementType": { "kind": "struct", "name": "...", "fields": [
//             { "name": "...", "type": { "kind": "vector"|"scalar"|"matrix",
//             ... },
//               "binding": { "kind": "uniform", "offset": N_bytes, "size":
//               M_bytes } }
//         ] },
//         // Resource (texture or structured buffer):
//         "baseShape": "texture2D" | "structuredBuffer" | "byteAddressBuffer" |
//         ..., "access": "readWrite" | "read" | ...,  // omitted for read-only
//         "resultType": { kind: "vector"|"scalar", ... },
//       }
//     }
//   ],
//   "entryPoints": [
//     {
//       "name": "...",
//       "stage": "vertex" | "fragment" | "compute",
//       "parameters": [        // varying inputs
//         {
//           "name": "...",
//           "binding": { "kind": "varyingInput", "index": N, "count"?: K },
//           "type": { "kind": "struct", "fields": [
//               { "name": "...", "type": {kind:"vector"|"scalar",
//               elementCount?: 2|3|4},
//                 "binding": { "kind": "varyingInput", "index": N },
//                 "semanticName": "POSITION" }
//           ] }
//         }
//       ],
//       "threadGroupSize": [x, y, z],   // compute only
//       "result": { ... varying outputs ... }
//     }
//   ]
// }
//
// Notes:
//  * Vertex inputs live under entryPoints[].parameters[] — NOT under the
//    top-level parameters[]. The latter only holds resources / UBs.
//  * The top-level parameter for a ConstantBuffer<T> has
//    binding.kind = "descriptorTableSlot" and type.kind = "constantBuffer".
//    We pull UB member layout out of type.elementType.fields[].
//  * For a Texture2D the param is descriptorTableSlot + type.kind=resource,
//    baseShape=texture2D. SamplerState gets its own descriptorTableSlot
//    entry with type.kind=samplerState.
//  * For RWStructuredBuffer<T>: type.kind=resource, baseShape=structuredBuffer,
//    access="readWrite". Read-only StructuredBuffer<T> omits the access key.

// Map a "type" node from the Slang reflection JSON to a float-component
// count (mat4 = 16, vec3 = 3, scalar = 1). Returns 0 for unrecognised
// shapes; caller may default to 4.
int comp_count_of_type_json(const json &t) {
  if (!t.is_object())
    return 0;
  std::string kind = t.value("kind", std::string(""));
  if (kind == "scalar")
    return 1;
  if (kind == "vector")
    return t.value("elementCount", 0);
  if (kind == "matrix") {
    int rc = t.value("rowCount", 0);
    int cc = t.value("columnCount", 0);
    return rc * cc;
  }
  if (kind == "array") {
    // e.g. float4x4 bones[8] = 128 floats (native 側 component_count_of と
    // 同じく flat な float 数として扱う)
    int ec = t.value("elementCount", 0);
    int inner = t.contains("elementType")
                    ? comp_count_of_type_json(t["elementType"])
                    : 0;
    return ec * inner;
  }
  return 0;
}

// Populate a ShaderUniformBlock from a top-level parameter whose type is
// a ConstantBuffer<T>. Reads members from type.elementType.fields[].
void fill_uniform_block_from_json(const json &p, int slot, SglShaderStage stage,
                                  ShaderUniformBlock *u) {
  u->slot = slot;
  u->stage = stage;
  u->size_floats = 0;
  u->member_count = 0;
  copy_name_capped(u->name, sizeof(u->name), p.value("name", std::string("")));

  if (!p.contains("type") || !p["type"].is_object())
    return;
  const json &t = p["type"];
  if (!t.contains("elementType") || !t["elementType"].is_object())
    return;
  const json &el = t["elementType"];
  if (el.value("kind", std::string("")) != "struct")
    return;
  if (!el.contains("fields") || !el["fields"].is_array())
    return;

  int total_bytes = 0;
  for (const auto &f : el["fields"]) {
    if (u->member_count >= SGL_MAX_UB_MEMBERS)
      break;
    if (!f.is_object())
      continue;
    ShaderUniformMember *m = &u->members[u->member_count++];
    copy_name_capped(m->name, sizeof(m->name),
                     f.value("name", std::string("")));
    int off_bytes = 0;
    int size_bytes = 0;
    if (f.contains("binding") && f["binding"].is_object()) {
      off_bytes = f["binding"].value("offset", 0);
      size_bytes = f["binding"].value("size", 0);
    }
    m->offset_floats = off_bytes / 4;
    int cc = f.contains("type") ? comp_count_of_type_json(f["type"]) : 0;
    if (cc <= 0)
      cc = (size_bytes + 3) / 4;
    m->comp_count = cc;
    int end_bytes = off_bytes + size_bytes;
    if (end_bytes > total_bytes)
      total_bytes = end_bytes;
  }
  u->size_floats = (total_bytes + 3) / 4;
}

// Top-level parameters[] walker. Each entry is either a UB, a texture, a
// sampler, or a storage buffer. We dedup by slot so cross-stage merges
// don't double-count. Samplers go to `samplers` for pair_samplers.
void process_global_parameter(const json &p, ShaderReflection *out,
                              SglShaderStage stage,
                              std::vector<ReflSampler> *samplers) {
  if (!p.is_object())
    return;
  if (!p.contains("binding") || !p["binding"].is_object())
    return;
  std::string bkind = p["binding"].value("kind", std::string(""));
  // Only descriptorTableSlot bindings are for resources; uniform/varyingInput
  // appear nested inside fields. Other bindings (rootConstant, etc.) are
  // outside current scope.
  if (bkind != "descriptorTableSlot")
    return;
  int slot = p["binding"].value("index", 0);

  const json *t = nullptr;
  if (p.contains("type") && p["type"].is_object())
    t = &p["type"];
  std::string tkind = t ? t->value("kind", std::string("")) : "";

  if (tkind == "constantBuffer") {
    if (ub_slot_exists(out, stage, slot))
      return;
    if (out->ub_count >= SGL_MAX_UNIFORM_BLOCKS)
      return;
    fill_uniform_block_from_json(p, slot, stage, &out->ubs[out->ub_count++]);
    return;
  }
  if (tkind == "resource") {
    std::string shape = t->value("baseShape", std::string(""));
    if (shape == "structuredBuffer" || shape == "byteAddressBuffer") {
      if (sbuf_slot_exists(out, stage, slot))
        return;
      if (out->storage_buf_count >= SGL_MAX_STORAGE_BUFS)
        return;
      ShaderStorageBuf *sb = &out->storage_bufs[out->storage_buf_count++];
      copy_name_capped(sb->name, sizeof(sb->name),
                       p.value("name", std::string("")));
      sb->slot = slot;
      sb->stage = stage;
      // access="readWrite" => writable; absent or "read" => readonly.
      sb->readonly = (t->value("access", std::string("")) != "readWrite");
      return;
    }
    std::string access = t->value("access", std::string(""));
    if (access == "readWrite" || access == "write" || access == "writeOnly") {
      if (stex_slot_exists(out, stage, slot))
        return;
      if (out->storage_tex_count >= SGL_MAX_STORAGE_TEXTURES)
        return;
      ShaderStorageTexture *st = &out->storage_texs[out->storage_tex_count++];
      copy_name_capped(st->name, sizeof(st->name),
                       p.value("name", std::string("")));
      st->slot = slot;
      st->stage = stage;
      st->access_format = SGL_PF_RGBA16F;
      if (t->contains("resultType") && (*t)["resultType"].is_object()) {
        const json &rt = (*t)["resultType"];
        std::string stype;
        int elems = 1;
        if (rt.value("kind", std::string("")) == "vector") {
          elems = rt.value("elementCount", 1);
          if (rt.contains("elementType") && rt["elementType"].is_object())
            stype = rt["elementType"].value("scalarType", std::string(""));
        } else {
          stype = rt.value("scalarType", std::string(""));
        }
        if (stype == "float32" && elems == 4)
          st->access_format = SGL_PF_RGBA32F;
        else if (stype == "float16" && elems == 4)
          st->access_format = SGL_PF_RGBA16F;
      }
      st->readonly = false;
      return;
    }
    // Texture (baseShape="texture2D"/"texture3D"/...). Sampler usually
    // comes via a separate parameter with type.kind="samplerState", but a
    // combined `Sampler2D<>` source has Slang emit "combined": true and
    // serves both image and sampler from a single descriptor slot.
    if (tex_slot_exists(out, stage, slot))
      return;
    if (out->tex_count >= SGL_MAX_TEXTURES)
      return;
    ShaderTexture *tx = &out->texs[out->tex_count++];
    copy_name_capped(tx->name, sizeof(tx->name),
                     p.value("name", std::string("")));
    tx->smp_name[0] = '\0';
    tx->img_slot = slot;
    tx->stage = stage;
    bool combined = t->value("combined", false);
    tx->smp_slot = combined ? slot : -1;
    return;
  }
  if (tkind == "samplerState")
    samplers->push_back({p.value("name", std::string("")), slot});
}

// Populate ShaderReflection from a Slang reflection JSON document.
//
// Slang WGSL emit puts UBs / textures / samplers / storage in @group(0).
// Sokol-gfx's WGPU backend expects UBs in @group(0) and the rest in
// @group(1). The slang-bridge.ts post-processor patches the WGSL group
// indices; the reflection here remains in Slang's native indexing
// (descriptorTableSlot index), since that's the @binding number which
// is preserved across the rewrite.
bool reflect_from_slang_json(const char *json_text, ShaderReflection *out,
                             SglShaderStage reflect_stage, char *err,
                             size_t errsz) {
  if (!out)
    return false;
  if (!json_text || !*json_text)
    return true; // empty JSON -> nothing to merge

  json j = json::parse(json_text, nullptr, false);
  if (j.is_discarded()) {
    if (err && errsz)
      snprintf(err, errsz, "slang reflection parse failed");
    return false;
  }

  // 1. Top-level parameters[]: UBs, textures, samplers, storage buffers.
  if (j.contains("parameters") && j["parameters"].is_array()) {
    std::vector<ReflSampler> samplers;
    for (const auto &p : j["parameters"])
      process_global_parameter(p, out, reflect_stage, &samplers);
    if (!pair_samplers(out, reflect_stage, samplers, err, errsz))
      return false;
  }

  // 2. entryPoints[]: varying inputs (vertex only) and threadGroupSize
  //    (compute only).
  if (j.contains("entryPoints") && j["entryPoints"].is_array()) {
    for (const auto &ep : j["entryPoints"]) {
      if (!ep.is_object())
        continue;
      std::string stage = ep.value("stage", std::string(""));

      // Compute work-group size.
      if (stage == "compute" && ep.contains("threadGroupSize") &&
          ep["threadGroupSize"].is_array() &&
          ep["threadGroupSize"].size() == 3) {
        out->is_compute = true;
        for (int k = 0; k < 3; ++k) {
          out->workgroup[k] = ep["threadGroupSize"][k].get<int>();
        }
      }
    }
  }
  // Slang assigns a single contiguous binding index across ALL resource types
  // (textures, samplers, UBs share one counter). After TS-side remapWgslGroups
  // splits UBs to @group(0) and textures/samplers/storage to @group(1), the
  // binding indices within each group must be compacted to 0, 1, 2, ...
  // Otherwise a shader with 4 textures and 1 UB ends up with UB @binding(4)
  // which can exceed SG_MAX_UNIFORMBLOCK_BINDSLOTS.
  for (int i = 0; i < out->ub_count; ++i)
    out->ubs[i].slot = i;
  {
    int next = 0;
    for (int i = 0; i < out->tex_count; ++i) {
      out->texs[i].img_slot = next++;
      if (out->texs[i].smp_slot >= 0)
        out->texs[i].smp_slot = next++;
    }
    for (int i = 0; i < out->storage_buf_count; ++i)
      out->storage_bufs[i].slot = next++;
    for (int i = 0; i < out->storage_tex_count; ++i)
      out->storage_texs[i].slot = next++;
  }
  return true;
}

static bool wasm_ub_slot_used(const ShaderReflection *refl, int slot) {
  for (int i = 0; i < refl->ub_count; ++i) {
    if (refl->ubs[i].slot == slot)
      return true;
  }
  return false;
}

static bool wasm_binding_used(const ShaderReflection *refl, int slot) {
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].img_slot == slot || refl->texs[i].smp_slot == slot)
      return true;
  }
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (refl->storage_bufs[i].slot == slot)
      return true;
  }
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    if (refl->storage_texs[i].slot == slot)
      return true;
  }
  return false;
}

static int wasm_next_free_ub_slot(const ShaderReflection *dst,
                                  const ShaderReflection *stage, int upto) {
  for (int s = 0; s < SGL_MAX_UNIFORM_BLOCKS; ++s) {
    bool used = wasm_ub_slot_used(dst, s);
    for (int i = 0; i < upto && !used; ++i)
      used = stage->ubs[i].slot == s;
    if (!used)
      return s;
  }
  return -1;
}

// Group 1 is shared by every stage's textures, samplers, storage buffers and
// storage textures, so the free-slot search must cover their sum, not one
// resource type's cap (a post pass with four textures plus the vertex
// stage's storage buffer already needs nine slots).
#define WASM_MAX_GROUP1_BINDINGS 64

static int wasm_next_free_binding(const ShaderReflection *dst,
                                  const ShaderReflection *stage) {
  for (int s = 0; s < WASM_MAX_GROUP1_BINDINGS; ++s) {
    if (!wasm_binding_used(dst, s) && !wasm_binding_used(stage, s))
      return s;
  }
  return -1;
}

// WGSL bind-slot remap: keep module-wide ub/texture/sampler bindings unique
// across the merged VS+FS reflection. The convention originates from
// sokol-gfx's WGPU backend and is mirrored by web/playground/slang-bridge.ts
// and backend_webgpu.c's bind group layout.
static void wasm_remap_stage_for_wgsl(const ShaderReflection *dst,
                                      ShaderReflection *stage) {
  for (int i = 0; i < stage->ub_count; ++i) {
    if (wasm_ub_slot_used(dst, stage->ubs[i].slot)) {
      int slot = wasm_next_free_ub_slot(dst, stage, i);
      if (slot >= 0)
        stage->ubs[i].slot = slot;
    }
  }
  for (int i = 0; i < stage->tex_count; ++i) {
    if (wasm_binding_used(dst, stage->texs[i].img_slot)) {
      int slot = wasm_next_free_binding(dst, stage);
      if (slot >= 0)
        stage->texs[i].img_slot = slot;
    }
    if (stage->texs[i].smp_slot >= 0 &&
        (wasm_binding_used(dst, stage->texs[i].smp_slot) ||
         stage->texs[i].smp_slot == stage->texs[i].img_slot)) {
      int slot = wasm_next_free_binding(dst, stage);
      if (slot >= 0)
        stage->texs[i].smp_slot = slot;
    }
  }
  for (int i = 0; i < stage->storage_buf_count; ++i) {
    if (wasm_binding_used(dst, stage->storage_bufs[i].slot)) {
      int slot = wasm_next_free_binding(dst, stage);
      if (slot >= 0)
        stage->storage_bufs[i].slot = slot;
    }
  }
  for (int i = 0; i < stage->storage_tex_count; ++i) {
    if (wasm_binding_used(dst, stage->storage_texs[i].slot)) {
      int slot = wasm_next_free_binding(dst, stage);
      if (slot >= 0)
        stage->storage_texs[i].slot = slot;
    }
  }
}

static void wasm_merge_stage_reflection(ShaderReflection *dst,
                                        const ShaderReflection *src,
                                        ShaderTargetBackend target) {
  ShaderReflection stage = *src;
  if (target == SHADER_TARGET_WGSL)
    wasm_remap_stage_for_wgsl(dst, &stage);
  for (int i = 0; i < stage.ub_count && dst->ub_count < SGL_MAX_UNIFORM_BLOCKS;
       ++i)
    dst->ubs[dst->ub_count++] = stage.ubs[i];
  for (int i = 0; i < stage.tex_count && dst->tex_count < SGL_MAX_TEXTURES; ++i)
    dst->texs[dst->tex_count++] = stage.texs[i];
  for (int i = 0; i < stage.storage_buf_count &&
                  dst->storage_buf_count < SGL_MAX_STORAGE_BUFS;
       ++i)
    dst->storage_bufs[dst->storage_buf_count++] = stage.storage_bufs[i];
  for (int i = 0; i < stage.storage_tex_count &&
                  dst->storage_tex_count < SGL_MAX_STORAGE_TEXTURES;
       ++i)
    dst->storage_texs[dst->storage_tex_count++] = stage.storage_texs[i];
  if (stage.is_compute) {
    dst->is_compute = true;
    dst->workgroup[0] = stage.workgroup[0];
    dst->workgroup[1] = stage.workgroup[1];
    dst->workgroup[2] = stage.workgroup[2];
  }
}

// Slang WASM appends _0, _1, ... suffixes to WGSL identifiers to avoid
// collisions, but reflection JSON keeps the original source names.  Strip
// a single trailing _\d+ so the WGSL name `scene_0` matches reflection
// name `scene`, while a user-defined `pos_1` still matches `pos_1` first.
static bool wasm_name_matches(const std::string &wgsl_name,
                              const char *refl_name) {
  if (wgsl_name == refl_name)
    return true;
  size_t last_us = wgsl_name.rfind('_');
  if (last_us == std::string::npos || last_us == 0 ||
      last_us + 1 >= wgsl_name.size())
    return false;
  bool all_digits = true;
  for (size_t i = last_us + 1; i < wgsl_name.size(); ++i)
    if (wgsl_name[i] < '0' || wgsl_name[i] > '9')
      all_digits = false;
  return all_digits && wgsl_name.substr(0, last_us) == refl_name;
}

static bool wasm_reflected_binding_for_name(const ShaderReflection *refl,
                                            SglShaderStage stage,
                                            const std::string &name,
                                            int *out_binding) {
  for (int i = 0; i < refl->ub_count; ++i) {
    const ShaderUniformBlock *u = &refl->ubs[i];
    if (u->stage == stage && wasm_name_matches(name, u->name)) {
      *out_binding = u->slot;
      return true;
    }
  }
  for (int i = 0; i < refl->tex_count; ++i) {
    const ShaderTexture *t = &refl->texs[i];
    if (t->stage != stage)
      continue;
    if (wasm_name_matches(name, t->name)) {
      *out_binding = t->img_slot;
      return true;
    }
    if (t->smp_name[0] && wasm_name_matches(name, t->smp_name)) {
      *out_binding = t->smp_slot;
      return true;
    }
  }
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    const ShaderStorageBuf *b = &refl->storage_bufs[i];
    if (b->stage == stage && wasm_name_matches(name, b->name)) {
      *out_binding = b->slot;
      return true;
    }
  }
  for (int i = 0; i < refl->storage_tex_count; ++i) {
    const ShaderStorageTexture *t = &refl->storage_texs[i];
    if (t->stage == stage && wasm_name_matches(name, t->name)) {
      *out_binding = t->slot;
      return true;
    }
  }
  return false;
}

static std::string wasm_extract_var_name(const std::string &line) {
  size_t p = line.find(" var");
  if (p == std::string::npos)
    return "";
  p += 4;
  if (p < line.size() && line[p] == '<') {
    size_t e = line.find('>', p);
    if (e == std::string::npos)
      return "";
    p = e + 1;
  }
  while (p < line.size() && (line[p] == ' ' || line[p] == '\t'))
    p++;
  size_t start = p;
  while (p < line.size() &&
         ((line[p] >= 'A' && line[p] <= 'Z') ||
          (line[p] >= 'a' && line[p] <= 'z') ||
          (line[p] >= '0' && line[p] <= '9') || line[p] == '_')) {
    p++;
  }
  return p > start ? line.substr(start, p - start) : "";
}

static void patch_wgsl_bindings_from_reflection(ShaderBlob *blob,
                                                SglShaderStage stage,
                                                const ShaderReflection *refl) {
  if (!blob || !blob->spirv || !refl)
    return;
  std::string src((const char *)blob->spirv, blob->bytes);
  std::string out;
  out.reserve(src.size());
  size_t pos = 0;
  while (pos < src.size()) {
    size_t end = src.find('\n', pos);
    if (end == std::string::npos)
      end = src.size();
    std::string line = src.substr(pos, end - pos);
    size_t b0 = line.find("@binding(");
    if (b0 != std::string::npos) {
      size_t n0 = b0 + strlen("@binding(");
      size_t n1 = line.find(')', n0);
      std::string name = wasm_extract_var_name(line);
      int binding = -1;
      if (n1 != std::string::npos &&
          wasm_reflected_binding_for_name(refl, stage, name, &binding) &&
          binding >= 0) {
        line.replace(n0, n1 - n0, std::to_string(binding));
      }
    }
    out.append(line);
    if (end < src.size())
      out.push_back('\n');
    pos = end + 1;
  }
  uint32_t *patched = (uint32_t *)malloc(out.size() + 1);
  if (!patched)
    return;
  memcpy(patched, out.data(), out.size());
  ((char *)patched)[out.size()] = '\0';
  free(blob->spirv);
  blob->spirv = patched;
  blob->bytes = out.size();
}

// Common driver: call the JS bridge once, split, and copy WGSL bytes into
// `out_blob`. Reflection JSON is returned via `out_refl_json` for the caller
// to merge into ShaderReflection.
bool compile_one(const char *src, const char *entry, int stage,
                 ShaderBlob *out_blob, std::string &out_refl_json,
                 char *err_buf, size_t err_buf_size) {
  char *blob = lub_slang_compile_js(src, entry, stage);
  if (!blob) {
    // NULL is reserved for genuinely unrecoverable cases (alloc failure
    // inside the JS shim, runtime not initialised). Anything else comes
    // back as a 0x02-prefixed error payload — see the EM_ASYNC_JS contract.
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size,
               "slang(%s) compile: out of memory or runtime not initialised",
               entry);
    }
    return false;
  }
  // 0x02 leading byte signals a Slang diagnostic; the rest is the message.
  if (blob[0] == '\x02') {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size, "slang(%s) %s", entry, blob + 1);
    }
    free(blob);
    return false;
  }
  std::string wgsl;
  bool ok = split_blob(blob, wgsl, out_refl_json);
  free(blob);
  if (!ok) {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size, "slang(%s) returned malformed blob",
               entry);
    }
    return false;
  }
  // Stash WGSL source bytes into out_blob->spirv. The field is misnamed on
  // wasm (it's WGSL text, not SPIR-V binary) but it's the same opaque byte
  // container the backend consumes.
  size_t n = wgsl.size();
  out_blob->spirv = (uint32_t *)malloc(n + 1);
  if (!out_blob->spirv) {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size, "OOM copying WGSL (%zu bytes)", n);
    }
    return false;
  }
  memcpy(out_blob->spirv, wgsl.data(), n);
  ((char *)out_blob->spirv)[n] = '\0';
  out_blob->bytes = n;
  return true;
}

} // anonymous namespace

extern "C" bool shader_compile(const char *vs_src, const char *fs_src,
                               ShaderTargetBackend target, ShaderBlob *out_vs,
                               ShaderBlob *out_fs, ShaderReflection *out_refl,
                               char *err_buf, size_t err_buf_size) {
  // wasm emits WGSL; descriptor-set patching N/A. But the LUB_TEXTURE2D /
  // LUB_SAMPLE macros still need expanding so the shader source can be
  // shared with native sdlgpu/d3d12 builds.
  if (out_vs) {
    out_vs->spirv = nullptr;
    out_vs->bytes = 0;
  }
  if (out_fs) {
    out_fs->spirv = nullptr;
    out_fs->bytes = 0;
  }
  if (out_refl)
    memset(out_refl, 0, sizeof(*out_refl));

  const char *prelude = prelude_for_target(target);
  std::string vs_with_prelude = std::string(prelude) + vs_src;
  std::string fs_with_prelude = std::string(prelude) + fs_src;

  std::string vs_refl_json, fs_refl_json;
  if (!compile_one(vs_with_prelude.c_str(), "vs_main", SLANG_BRIDGE_STAGE_VS,
                   out_vs, vs_refl_json, err_buf, err_buf_size)) {
    return false;
  }
  if (!compile_one(fs_with_prelude.c_str(), "fs_main", SLANG_BRIDGE_STAGE_FS,
                   out_fs, fs_refl_json, err_buf, err_buf_size)) {
    free(out_vs->spirv);
    out_vs->spirv = nullptr;
    out_vs->bytes = 0;
    return false;
  }

  ShaderReflection vs_refl;
  ShaderReflection fs_refl;
  memset(&vs_refl, 0, sizeof(vs_refl));
  memset(&fs_refl, 0, sizeof(fs_refl));
  if (!reflect_from_slang_json(vs_refl_json.c_str(), &vs_refl, SGL_STAGE_VERTEX,
                               err_buf, err_buf_size) ||
      !reflect_from_slang_json(fs_refl_json.c_str(), &fs_refl,
                               SGL_STAGE_FRAGMENT, err_buf, err_buf_size)) {
    free(out_vs->spirv);
    out_vs->spirv = nullptr;
    out_vs->bytes = 0;
    free(out_fs->spirv);
    out_fs->spirv = nullptr;
    out_fs->bytes = 0;
    return false;
  }
  wasm_merge_stage_reflection(out_refl, &vs_refl, target);
  wasm_merge_stage_reflection(out_refl, &fs_refl, target);
  patch_wgsl_bindings_from_reflection(out_vs, SGL_STAGE_VERTEX, out_refl);
  patch_wgsl_bindings_from_reflection(out_fs, SGL_STAGE_FRAGMENT, out_refl);
  return true;
}

extern "C" bool shader_compile_compute(const char *cs_src,
                                       ShaderTargetBackend target,
                                       ShaderBlob *out_cs,
                                       ShaderReflection *out_refl,
                                       char *err_buf, size_t err_buf_size) {
  if (out_cs) {
    out_cs->spirv = nullptr;
    out_cs->bytes = 0;
  }
  if (out_refl)
    memset(out_refl, 0, sizeof(*out_refl));

  const char *prelude = prelude_for_target(target);
  std::string cs_with_prelude = std::string(prelude) + cs_src;

  std::string cs_refl_json;
  if (!compile_one(cs_with_prelude.c_str(), "cs_main", SLANG_BRIDGE_STAGE_CS,
                   out_cs, cs_refl_json, err_buf, err_buf_size)) {
    return false;
  }
  out_refl->is_compute = true;
  out_refl->workgroup[0] = out_refl->workgroup[1] = out_refl->workgroup[2] = 1;
  if (!reflect_from_slang_json(cs_refl_json.c_str(), out_refl,
                               SGL_STAGE_COMPUTE, err_buf, err_buf_size)) {
    free(out_cs->spirv);
    out_cs->spirv = nullptr;
    out_cs->bytes = 0;
    return false;
  }
  patch_wgsl_bindings_from_reflection(out_cs, SGL_STAGE_COMPUTE, out_refl);
  return true;
}

extern "C" void shader_blob_free(ShaderBlob *b) {
  if (!b)
    return;
  if (b->spirv) {
    free(b->spirv);
    b->spirv = nullptr;
  }
  b->bytes = 0;
}

#endif // __EMSCRIPTEN__

#ifndef __EMSCRIPTEN__
// Native entry points: shader cache first, then Slang when this player has
// it. A cache directory named by LUB_SHADER_CACHE is written through.
extern "C" void shader_blob_free(ShaderBlob *b) {
  if (!b)
    return;
  if (b->spirv) {
    free(b->spirv);
    b->spirv = nullptr;
  }
  b->bytes = 0;
}

[[maybe_unused]] static bool cache_miss(const char *key, char *err_buf,
                                        size_t err_buf_size) {
  if (err_buf && err_buf_size)
    snprintf(err_buf, err_buf_size,
             "this player has no shader compiler and shader-cache/%s.lubshader "
             "is missing; run the game once on a player with Slang and "
             "LUB_SHADER_CACHE set to fill the cache",
             key);
  return false;
}

extern "C" bool shader_compile(const char *vs_src, const char *fs_src,
                               ShaderTargetBackend target, ShaderBlob *out_vs,
                               ShaderBlob *out_fs, ShaderReflection *out_refl,
                               char *err_buf, size_t err_buf_size) {
  *out_vs = {};
  *out_fs = {};
  memset(out_refl, 0, sizeof(*out_refl));
  const char *sources[] = {vs_src, fs_src};
  ShaderBlob *blobs[] = {out_vs, out_fs};
  char key[SHADER_CACHE_KEY_CHARS];
  shader_cache_key(target, sources, 2, key);
  if (shader_cache_load(key, blobs, 2, out_refl))
    return true;
#ifdef LUB_HAS_SLANG
  if (!compile_graphics(vs_src, fs_src, target, out_vs, out_fs, out_refl,
                        err_buf, err_buf_size))
    return false;
  shader_cache_store(key, blobs, 2, out_refl);
  return true;
#else
  return cache_miss(key, err_buf, err_buf_size);
#endif
}

extern "C" bool shader_compile_compute(const char *cs_src,
                                       ShaderTargetBackend target,
                                       ShaderBlob *out_cs,
                                       ShaderReflection *out_refl,
                                       char *err_buf, size_t err_buf_size) {
  *out_cs = {};
  memset(out_refl, 0, sizeof(*out_refl));
  const char *sources[] = {cs_src};
  ShaderBlob *blobs[] = {out_cs};
  char key[SHADER_CACHE_KEY_CHARS];
  shader_cache_key(target, sources, 1, key);
  if (shader_cache_load(key, blobs, 1, out_refl))
    return true;
#ifdef LUB_HAS_SLANG
  if (!compile_compute(cs_src, target, out_cs, out_refl, err_buf, err_buf_size))
    return false;
  shader_cache_store(key, blobs, 1, out_refl);
  return true;
#else
  return cache_miss(key, err_buf, err_buf_size);
#endif
}
#endif // !__EMSCRIPTEN__
