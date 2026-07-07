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

#ifndef __EMSCRIPTEN__
#include <slang-com-ptr.h>
#include <slang.h>
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

// Per-target shader prelude. SDL_GPU wants textures and samplers as
// VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER at a single binding; DX12 and
// WGSL use separate texture + sampler declarations. The macros let one
// shader source compile to both layouts without per-target #ifdef.
//
//   LUB_TEXTURE2D(diffuse);          // dx12/wgsl: Texture2D + SamplerState
//                                    // pair; sdlgpu: Sampler2D<float4>.
//   color = LUB_SAMPLE(diffuse, uv); // expands to the right Sample() call.
//
// Wasm (WGSL via slang-wasm) uses the separate form: Slang auto-lowers it
// for WGSL and the declaration shape stays consistent across native + wasm
// reflection output.
static const char *prelude_for_target(ShaderTargetBackend target) {
  // LUB_SAMPLE_LOD is an explicit-LOD (level 0) sample. Textures here are never
  // mipmapped so it's equivalent to LUB_SAMPLE, but unlike implicit-LOD Sample
  // it is legal in non-uniform control flow — WGSL (WebGPU) rejects an
  // implicit-LOD textureSample after a data-dependent branch/loop, which the
  // post passes (SSAO/outline/water) hit when they sample after an early-out.
  // Native SDL_GPU/Vulkan tolerates implicit-LOD there, so a native-only run
  // passes and the pipeline only turns up invalid (black screen) on web: use
  // LUB_SAMPLE_LOD for any sample reached after a branch/loop.
  if (target == SHADER_TARGET_SDLGPU) {
    return "#define LUB_TEXTURE2D(n) Sampler2D<float4> n\n"
           "#define LUB_SAMPLE(t, uv) t.Sample(uv)\n"
           "#define LUB_SAMPLE_LOD(t, uv) t.SampleLevel(uv, 0.0)\n";
  }
  // wasm and dx12 use the separate texture+sampler form (D3D12 has no
  // combined image samplers; t/s registers are distinct classes).
  return "#define LUB_TEXTURE2D(n) Texture2D n; SamplerState n##_smp\n"
         "#define LUB_SAMPLE(t, uv) t.Sample(t##_smp, uv)\n"
         "#define LUB_SAMPLE_LOD(t, uv) t.SampleLevel(t##_smp, uv, 0.0)\n";
}

#ifndef __EMSCRIPTEN__
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
  if (backend == SHADER_TARGET_DX12) {
    target->format = SLANG_DXIL;
    target->profile = g_slang.dxil_profile;
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

bool fill_attrs_from_entry_point(EntryPointReflection *ep,
                                 ShaderReflection *out, char *err,
                                 size_t errsz) {
  out->attr_count = 0;
  out->vertex_stride_floats = 0;
  out->buffer_count = 0;
  memset(out->buffer_stride_floats, 0, sizeof(out->buffer_stride_floats));
  if (!ep) {
    if (err && errsz)
      snprintf(err, errsz, "no vertex entry point reflection");
    return false;
  }
  unsigned pcount = ep->getParameterCount();
  int input_buffer = 0;
  for (unsigned i = 0; i < pcount; ++i) {
    VariableLayoutReflection *p = ep->getParameterByIndex(i);
    if (!p)
      continue;
    TypeLayoutReflection *tl = p->getTypeLayout();
    if (!tl)
      continue;
    TypeReflection *t = tl->getType();
    if (!t)
      continue;

    // Two cases:
    //  (a) parameter is a struct: iterate its fields as varying inputs.
    //  (b) parameter is a vector/scalar: it's a single varying input.
    if (input_buffer >= SGL_MAX_VERTEX_BUFFERS) {
      if (err && errsz)
        snprintf(err, errsz, "too many vertex input buffers (>%d)",
                 SGL_MAX_VERTEX_BUFFERS);
      return false;
    }
    const int buffer_index = input_buffer;
    bool recorded_any = false;
    auto record_one = [&](const char *name, TypeReflection *vt,
                          const char *semantic, size_t semantic_index) -> bool {
      if (out->attr_count >= SGL_MAX_ATTRS) {
        if (err && errsz)
          snprintf(err, errsz, "too many vertex attributes (>%d)",
                   SGL_MAX_ATTRS);
        return false;
      }
      ShaderAttr *a = &out->attrs[out->attr_count];
      copy_name(a->name, sizeof(a->name), name);
      // Canonicalize "TEXCOORD0" style semantics into base + index so the
      // dx12 input layout matches the DXIL input signature.
      copy_name(a->semantic, sizeof(a->semantic), semantic);
      a->semantic_index = (int)semantic_index;
      size_t sn = strlen(a->semantic);
      size_t digits = 0;
      while (digits < sn && a->semantic[sn - 1 - digits] >= '0' &&
             a->semantic[sn - 1 - digits] <= '9')
        digits++;
      if (digits > 0 && digits < sn) {
        a->semantic_index = atoi(a->semantic + sn - digits);
        a->semantic[sn - digits] = '\0';
      }
      a->slot = out->attr_count; // input location: assume sequential
      a->comp_count = component_count_of(vt);
      if (a->comp_count <= 0)
        a->comp_count = 4;
      a->buffer_index = buffer_index;
      a->offset_floats = out->buffer_stride_floats[buffer_index];
      out->buffer_stride_floats[buffer_index] += a->comp_count;
      out->attr_count++;
      recorded_any = true;
      return true;
    };

    if (t->getKind() == TypeReflection::Kind::Struct) {
      unsigned fc = tl->getFieldCount();
      for (unsigned f = 0; f < fc; ++f) {
        VariableLayoutReflection *fl = tl->getFieldByIndex(f);
        if (!fl)
          continue;
        TypeReflection *ft = fl->getTypeLayout()->getType();
        if (!record_one(fl->getName() ? fl->getName() : "attr", ft,
                        fl->getSemanticName() ? fl->getSemanticName() : "",
                        fl->getSemanticIndex()))
          return false;
      }
    } else {
      if (!record_one(p->getName() ? p->getName() : "attr", t,
                      p->getSemanticName() ? p->getSemanticName() : "",
                      p->getSemanticIndex()))
        return false;
    }
    if (recorded_any) {
      input_buffer++;
      out->buffer_count = input_buffer;
    }
  }
  out->vertex_stride_floats = out->buffer_stride_floats[0];
  return true;
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

// Record one global (module-scope) shader parameter into the reflection,
// attributed to `stage`. Sampler states pair positionally with the preceding
// textures of the same stage, so callers must feed a stage's parameters in
// declaration order.
void fill_global_param(VariableLayoutReflection *p, ShaderReflection *out,
                       SglShaderStage stage) {
  {
    SlangParameterCategory cat = (SlangParameterCategory)p->getCategory();
    TypeReflection *t =
        p->getTypeLayout() ? p->getTypeLayout()->getType() : nullptr;

    // Constant buffers / uniform blocks
    if (cat == SLANG_PARAMETER_CATEGORY_CONSTANT_BUFFER ||
        (t && t->getKind() == TypeReflection::Kind::ConstantBuffer)) {
      if (out->ub_count < SGL_MAX_UNIFORM_BLOCKS &&
          !refl_ub_exists(out, stage, (int)p->getBindingIndex(),
                          p->getName())) {
        fill_uniform_block(p, stage, &out->ubs[out->ub_count]);
        out->ub_count++;
      }
      return;
    }

    // Structured / RW structured buffers. Slang reports StructuredBuffer<T>
    // under SHADER_RESOURCE (resource shape == STRUCTURED_BUFFER) and
    // RWStructuredBuffer<T> under UNORDERED_ACCESS. Both feed the same
    // storage-buffer plumbing on our backends, distinguished by `readonly`.
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
            sb->elem_stride =
                (int)el->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
          }
        }
      }
      return;
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
        // for the matching SAMPLER_STATE param below.
        bool combined = false;
        if (t && t->getKind() == TypeReflection::Kind::Resource) {
          unsigned shape = (unsigned)t->getResourceShape();
          combined = (shape & SLANG_TEXTURE_COMBINED_FLAG) != 0;
        }
        tx->smp_slot = combined ? tx->img_slot : -1;
      }
      return;
    }

    // Sampler states — pair with the next unmatched texture in declaration
    // order. Slang/HLSL forbids identical names for separate Texture2D and
    // SamplerState parameters, so name-based matching can never succeed.
    // Instead we always pair positionally: the i-th texture is assigned the
    // i-th sampler. This works for the typical "Texture2D foo;
    // SamplerState foo_smp;" pattern. If shaders use combined samplers
    // (GLSL-style sampler2D) Slang may not produce separate parameters at
    // all.
    if (cat == SLANG_PARAMETER_CATEGORY_SAMPLER_STATE ||
        (t && t->getKind() == TypeReflection::Kind::SamplerState)) {
      int sidx = (int)p->getBindingIndex();
      int matched = -1;
      for (int k = 0; k < out->tex_count; ++k) {
        if (out->texs[k].stage == stage && out->texs[k].smp_slot < 0) {
          matched = k;
          break;
        }
      }
      if (matched >= 0)
        out->texs[matched].smp_slot = sidx;
      return;
    }
  }
}

bool fill_global_reflection(ProgramLayout *layout, ShaderReflection *out,
                            SglShaderStage stage) {
  if (!layout)
    return false;
  unsigned gpc = layout->getParameterCount();
  for (unsigned i = 0; i < gpc; ++i) {
    VariableLayoutReflection *p = layout->getParameterByIndex(i);
    if (!p)
      continue;
    fill_global_param(p, out, stage);
  }
  return true;
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

static bool name_matches_sampler(const char *var_name, const char *tex_name) {
  if (!var_name || !tex_name)
    return false;
  size_t n = strlen(tex_name);
  return strncmp(var_name, tex_name, n) == 0 &&
         strcmp(var_name + n, "_smp") == 0;
}

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
    if (strcmp(t->name, name) == 0) {
      *out_set = sdl_set_for_stage_resource(stage);
      *out_binding = t->smp_slot;
      return true;
    }
    if (name_matches_sampler(name, t->name)) {
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
  // DX12: no remap. The slots are Slang's HLSL register indices and the
  // DXIL blob can't be re-numbered after the fact; the backend builds its
  // root signature from these values instead.

  if (stage.attr_count > 0) {
    dst->attr_count = stage.attr_count;
    memcpy(dst->attrs, stage.attrs, sizeof(stage.attrs));
    dst->buffer_count = stage.buffer_count;
    memcpy(dst->buffer_stride_floats, stage.buffer_stride_floats,
           sizeof(stage.buffer_stride_floats));
    dst->vertex_stride_floats = stage.vertex_stride_floats;
  }
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

// DX12 graphics path: VS+FS must be linked into ONE slang program. DXIL
// matches varyings between stages by hardware register (not by location as
// SPIR-V/Vulkan does), and separately-compiled programs pack their varying
// signatures independently — e.g. VS emits SV_Position at o0 pushing COLOR
// to o1 while the FS expects COLOR at v0. Linking both entry points lets
// Slang lay out one consistent inter-stage signature. Side effect: b/t/s/u
// registers become program-unique across stages, which the dx12 backend
// relies on (single root-signature tables with SHADER_VISIBILITY_ALL).
bool compile_dx12_graphics(const char *vs_src, const char *fs_src,
                           ShaderBlob *out_vs, ShaderBlob *out_fs,
                           ShaderReflection *out_refl, char *err_buf,
                           size_t err_buf_size) {
  TargetDesc slang_target = {};
  configure_target(&slang_target, SHADER_TARGET_DX12);
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

  const char *prelude = prelude_for_target(SHADER_TARGET_DX12);
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

  EntryPointReflection *vsRefl = nullptr;
  SlangUInt epc = layout->getEntryPointCount();
  for (SlangUInt i = 0; i < epc; ++i) {
    EntryPointReflection *epr = layout->getEntryPointByIndex(i);
    if (epr && epr->getStage() == SLANG_STAGE_VERTEX) {
      vsRefl = epr;
      break;
    }
  }
  if (!fill_attrs_from_entry_point(vsRefl, out_refl, err_buf, err_buf_size))
    return false;

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
    for (unsigned i = 0; i < gpc; ++i) {
      VariableLayoutReflection *p = layout->getParameterByIndex(i);
      if (!p || !p->getName())
        continue;
      if (!has(names, p->getName()))
        continue;
      fill_global_param(p, out_refl, stage);
    }
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
      snprintf(err_buf, err_buf_size, "OOM (dx12 blobs)");
    return false;
  }
  return true;
}

} // anonymous namespace

extern "C" bool shader_compile(const char *vs_src, const char *fs_src,
                               ShaderTargetBackend target, ShaderBlob *out_vs,
                               ShaderBlob *out_fs, ShaderReflection *out_refl,
                               char *err_buf, size_t err_buf_size) {
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

  if (!ensure_global_session()) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "createGlobalSession failed");
    return false;
  }

  if (target == SHADER_TARGET_DX12) {
    return compile_dx12_graphics(vs_src, fs_src, out_vs, out_fs, out_refl,
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

    if (sgl_stage == SGL_STAGE_VERTEX) {
      EntryPointReflection *vsRefl = nullptr;
      SlangUInt epc = programLayout->getEntryPointCount();
      for (SlangUInt i = 0; i < epc; ++i) {
        EntryPointReflection *epr = programLayout->getEntryPointByIndex(i);
        if (epr && epr->getStage() == SLANG_STAGE_VERTEX) {
          vsRefl = epr;
          break;
        }
      }
      if (!fill_attrs_from_entry_point(vsRefl, stage_refl, err_buf,
                                       err_buf_size)) {
        return false;
      }
    }
    fill_global_reflection(programLayout, stage_refl, sgl_stage);

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

  if (target != SHADER_TARGET_DX12) {
    patch_spirv_bindings_from_reflection(out_vs->spirv, out_vs->bytes,
                                         SpvStage::Vertex, out_refl);
    patch_spirv_bindings_from_reflection(out_fs->spirv, out_fs->bytes,
                                         SpvStage::Fragment, out_refl);
    patch_spirv_storage_image_formats(out_vs->spirv, out_vs->bytes,
                                      SpvStage::Vertex, out_refl);
    patch_spirv_storage_image_formats(out_fs->spirv, out_fs->bytes,
                                      SpvStage::Fragment, out_refl);
  }
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
  fill_global_reflection(programLayout, out_refl, SGL_STAGE_COMPUTE);
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

  if (target != SHADER_TARGET_DX12) {
    patch_spirv_bindings_from_reflection(out_cs->spirv, out_cs->bytes,
                                         SpvStage::Compute, out_refl);
    patch_spirv_storage_image_formats(out_cs->spirv, out_cs->bytes,
                                      SpvStage::Compute, out_refl);
  }
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

#else // __EMSCRIPTEN__

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

// Record a single varying-input field. Fields appear inside a struct's
// "fields" array, each with its own binding.{index, kind} and type.
void record_attr_field(const json &f, ShaderReflection *out, int buffer_index) {
  if (out->attr_count >= SGL_MAX_ATTRS)
    return;
  if (buffer_index < 0 || buffer_index >= SGL_MAX_VERTEX_BUFFERS)
    return;
  std::string name = f.value("name", std::string("attr"));
  int cc = 0;
  if (f.contains("type"))
    cc = comp_count_of_type_json(f["type"]);
  if (cc <= 0)
    cc = 4;

  ShaderAttr *a = &out->attrs[out->attr_count];
  copy_name_capped(a->name, sizeof(a->name), name);
  // Slang WASM reflection reports field binding.index as per-struct-relative,
  // not global WGSL @location. Using the relative index directly causes slot
  // collisions with multi-struct vertex inputs (e.g. instanced shaders with
  // separate vertex + instance structs). Always use sequential attr_count,
  // matching the native Slang API path in fill_attrs_from_entry_point.
  a->slot = out->attr_count;
  a->comp_count = cc;
  a->buffer_index = buffer_index;
  a->offset_floats = out->buffer_stride_floats[buffer_index];
  out->buffer_stride_floats[buffer_index] += cc;
  if (out->buffer_count < buffer_index + 1)
    out->buffer_count = buffer_index + 1;
  out->vertex_stride_floats = out->buffer_stride_floats[0];
  out->attr_count++;
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
// don't double-count.
void process_global_parameter(const json &p, ShaderReflection *out,
                              SglShaderStage stage) {
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
    tx->img_slot = slot;
    tx->stage = stage;
    bool combined = t->value("combined", false);
    tx->smp_slot = combined ? slot : -1;
    return;
  }
  if (tkind == "samplerState") {
    // Pair with the next unpaired texture in declaration order. This
    // matches the native (Slang COM API) path's behaviour and works for
    // the typical `Texture2D foo; SamplerState foo_smp;` convention.
    for (int k = 0; k < out->tex_count; ++k) {
      if (out->texs[k].stage == stage && out->texs[k].smp_slot < 0) {
        out->texs[k].smp_slot = slot;
        return;
      }
    }
  }
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
                             SglShaderStage reflect_stage) {
  if (!out)
    return false;
  if (!json_text || !*json_text)
    return true; // empty JSON -> nothing to merge

  json j = json::parse(json_text, nullptr, false);
  if (j.is_discarded()) {
    fprintf(stderr, "[lub] reflect_from_slang_json: parse failed\n");
    return false;
  }

  // 1. Top-level parameters[]: UBs, textures, samplers, storage buffers.
  if (j.contains("parameters") && j["parameters"].is_array()) {
    // Two passes so SamplerState entries can pair with already-recorded
    // Texture entries regardless of declaration order in the JSON.
    for (const auto &p : j["parameters"]) {
      if (!p.is_object())
        continue;
      const auto *t = p.contains("type") ? &p["type"] : nullptr;
      if (t && t->is_object() &&
          t->value("kind", std::string("")) == "samplerState") {
        continue; // handled in second pass
      }
      process_global_parameter(p, out, reflect_stage);
    }
    for (const auto &p : j["parameters"]) {
      if (!p.is_object())
        continue;
      const auto *t = p.contains("type") ? &p["type"] : nullptr;
      if (t && t->is_object() &&
          t->value("kind", std::string("")) == "samplerState") {
        process_global_parameter(p, out, reflect_stage);
      }
    }
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

      // Varying inputs (vertex only). is_vertex_stage gates this — the
      // FS reflection has its own varyingInput entries (texcoords etc.)
      // but those aren't pipeline-level vertex attributes.
      if (reflect_stage != SGL_STAGE_VERTEX || stage != "vertex")
        continue;
      if (!ep.contains("parameters") || !ep["parameters"].is_array())
        continue;
      int input_buffer = 0;
      for (const auto &p : ep["parameters"]) {
        if (!p.is_object())
          continue;
        std::string bkind;
        if (p.contains("binding") && p["binding"].is_object()) {
          bkind = p["binding"].value("kind", std::string(""));
        }
        if (bkind != "varyingInput")
          continue;
        if (input_buffer >= SGL_MAX_VERTEX_BUFFERS)
          continue;
        // Two cases mirror the native path's fill_attrs_from_entry_point:
        //   (a) struct => flatten its fields[]
        //   (b) scalar/vector => single attr
        if (!p.contains("type") || !p["type"].is_object())
          continue;
        const json &t = p["type"];
        std::string tkind = t.value("kind", std::string(""));
        bool recorded_any = false;
        int before_count = out->attr_count;
        if (tkind == "struct" && t.contains("fields") &&
            t["fields"].is_array()) {
          for (const auto &f : t["fields"]) {
            record_attr_field(f, out, input_buffer);
          }
        } else {
          record_attr_field(p, out, input_buffer);
        }
        recorded_any = out->attr_count > before_count;
        if (recorded_any)
          input_buffer++;
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

static int wasm_next_free_binding(const ShaderReflection *dst,
                                  const ShaderReflection *stage) {
  for (int s = 0; s < SGL_MAX_TEXTURES; ++s) {
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
}

static void wasm_merge_stage_reflection(ShaderReflection *dst,
                                        const ShaderReflection *src,
                                        ShaderTargetBackend target) {
  ShaderReflection stage = *src;
  if (target == SHADER_TARGET_WGSL)
    wasm_remap_stage_for_wgsl(dst, &stage);
  if (stage.attr_count > 0) {
    dst->attr_count = stage.attr_count;
    memcpy(dst->attrs, stage.attrs, sizeof(stage.attrs));
    dst->buffer_count = stage.buffer_count;
    memcpy(dst->buffer_stride_floats, stage.buffer_stride_floats,
           sizeof(stage.buffer_stride_floats));
    dst->vertex_stride_floats = stage.vertex_stride_floats;
  }
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
    std::string smp = std::string(t->name) + "_smp";
    if (wasm_name_matches(name, smp.c_str())) {
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
  // shared with native sdlgpu/dx12 builds.
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
  if (!reflect_from_slang_json(vs_refl_json.c_str(), &vs_refl,
                               SGL_STAGE_VERTEX) ||
      !reflect_from_slang_json(fs_refl_json.c_str(), &fs_refl,
                               SGL_STAGE_FRAGMENT)) {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size, "slang reflection parse failed");
    }
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
                               SGL_STAGE_COMPUTE)) {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size,
               "slang reflection parse failed (compute)");
    }
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
