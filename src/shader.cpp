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

// Per-target shader prelude. sokol-gfx wants textures and samplers declared
// as separate Vulkan descriptors at distinct bindings; SDL_GPU wants them as
// VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER at a single binding. The macros
// let one shader source compile to both layouts without per-target #ifdef.
//
//   LUB_TEXTURE2D(diffuse);          // sokol: Texture2D + SamplerState pair;
//                                    // sdlgpu: Sampler2D<float4>.
//   color = LUB_SAMPLE(diffuse, uv); // expands to the right Sample() call.
//
// Wasm (WGSL via slang-wasm) is grouped with sokol: Slang auto-lowers
// combined samplers for WGSL so either prelude works; sokol-form keeps the
// declaration shape consistent across native + wasm reflection output.
static const char *prelude_for_target(ShaderTargetBackend target) {
  // LUB_SAMPLE_LOD is an explicit-LOD (level 0) sample. Textures here are never
  // mipmapped so it's equivalent to LUB_SAMPLE, but unlike implicit-LOD Sample
  // it is legal in non-uniform control flow — WGSL (WebGPU) rejects an
  // implicit-LOD textureSample after a data-dependent branch/loop, which the
  // post passes (SSAO/outline/water) hit when they sample after an early-out.
  if (target == SHADER_TARGET_SDLGPU) {
    return "#define LUB_TEXTURE2D(n) Sampler2D<float4> n\n"
           "#define LUB_SAMPLE(t, uv) t.Sample(uv)\n"
           "#define LUB_SAMPLE_LOD(t, uv) t.SampleLevel(uv, 0.0)\n";
  }
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
};

GlobalSlangCtx g_slang;

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
    auto record_one = [&](const char *name, TypeReflection *vt) -> bool {
      if (out->attr_count >= SGL_MAX_ATTRS) {
        if (err && errsz)
          snprintf(err, errsz, "too many vertex attributes (>%d)",
                   SGL_MAX_ATTRS);
        return false;
      }
      ShaderAttr *a = &out->attrs[out->attr_count];
      copy_name(a->name, sizeof(a->name), name);
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
        if (!record_one(fl->getName() ? fl->getName() : "attr", ft))
          return false;
      }
    } else {
      if (!record_one(p->getName() ? p->getName() : "attr", t))
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

bool fill_uniform_block(VariableLayoutReflection *p, ShaderUniformBlock *ub) {
  copy_name(ub->name, sizeof(ub->name), p->getName());
  ub->slot = (int)p->getBindingIndex();
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

bool fill_global_reflection(ProgramLayout *layout, ShaderReflection *out) {
  if (!layout)
    return false;
  out->ub_count = 0;
  out->tex_count = 0;
  out->storage_buf_count = 0;
  unsigned gpc = layout->getParameterCount();
  for (unsigned i = 0; i < gpc; ++i) {
    VariableLayoutReflection *p = layout->getParameterByIndex(i);
    if (!p)
      continue;
    SlangParameterCategory cat = (SlangParameterCategory)p->getCategory();
    TypeReflection *t =
        p->getTypeLayout() ? p->getTypeLayout()->getType() : nullptr;

    // Constant buffers / uniform blocks
    if (cat == SLANG_PARAMETER_CATEGORY_CONSTANT_BUFFER ||
        (t && t->getKind() == TypeReflection::Kind::ConstantBuffer)) {
      if (out->ub_count < SGL_MAX_UNIFORM_BLOCKS) {
        fill_uniform_block(p, &out->ubs[out->ub_count]);
        out->ub_count++;
      }
      continue;
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
      if (out->storage_buf_count < SGL_MAX_STORAGE_BUFS) {
        ShaderStorageBuf *sb = &out->storage_bufs[out->storage_buf_count++];
        copy_name(sb->name, sizeof(sb->name), p->getName());
        sb->slot = (int)p->getBindingIndex();
        sb->readonly = readonly;
      }
      continue;
    }

    // Textures (SRV) — record as a texture entry; sampler match is filled
    // below.
    if (cat == SLANG_PARAMETER_CATEGORY_SHADER_RESOURCE ||
        (t && t->getKind() == TypeReflection::Kind::Resource)) {
      if (out->tex_count < SGL_MAX_TEXTURES) {
        ShaderTexture *tx = &out->texs[out->tex_count++];
        copy_name(tx->name, sizeof(tx->name), p->getName());
        tx->img_slot = (int)p->getBindingIndex();
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
      continue;
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
        if (out->texs[k].smp_slot < 0) {
          matched = k;
          break;
        }
      }
      if (matched >= 0)
        out->texs[matched].smp_slot = sidx;
      continue;
    }
  }
  return true;
}

// Rewrite descriptor-set decorations in a SPIR-V module to match the target
// backend's expected Vulkan descriptor-set layout. Slang emits everything on
// set 0 by default, which causes VkPipelineLayout / SPIR-V mismatch panics on
// strict drivers. The two backends have different conventions:
//
//   sokol (single layout shared by VS+FS):
//     set 0: uniform buffers
//     set 1: textures, samplers, storage buffers
//
//   SDL_GPU (per-stage layout, see SDL_gpu.h CreateGPUShader docs):
//     vertex stage    -> set 0: textures/storage; set 1: uniform buffers
//     fragment stage  -> set 2: textures/samplers/storage; set 3: uniform
//     buffers compute stage   -> set 0: sampled textures + RO storage textures
//     + RO storage buffers
//                        set 1: RW storage textures + RW storage buffers
//                        set 2: uniform buffers
//
// SPIR-V structure: header = 5 words, then instructions where
// word0 = (wc<<16)|op. We do two passes:
//   1. Walk OpVariable to classify each resource ID by storage class.
//      - UniformConstant / StorageBuffer -> "image-ish" (texture/sampler/SSBO)
//      - Uniform                          -> "uniform-block"
//   2. For each OpDecorate %id DescriptorSet <n> we encounter, look up the
//      resource class and the desired set per the table above, then rewrite
//      the literal in place.
// Bindings, locations and other decorations are left untouched.
//
// The patch is split per-stage and per-backend because both blobs are passed
// in independently (one is VS-only, the other FS-only).
//
// Compute descriptor rewriting currently supports a single RW storage class
// (kind 0) plus an optional uniform block. Readonly storage buffers and
// storage textures need an additional kind to map them to SDL_GPU's compute
// set 0.
enum class SpvStage { Vertex, Fragment, Compute };

void patch_spirv_descriptor_sets(void *spv, size_t size_bytes,
                                 ShaderTargetBackend target, SpvStage stage) {
  if (!spv || size_bytes < 20)
    return; // too small to be valid
  uint32_t *words = (uint32_t *)spv;
  size_t nwords = size_bytes / 4;
  if (words[0] != 0x07230203u)
    return; // not a SPIR-V module

  // Opcodes / decorations / storage-classes we care about.
  constexpr uint32_t kOpTypePointer = 32;
  constexpr uint32_t kOpVariable = 59;
  constexpr uint32_t kOpDecorate = 71;
  constexpr uint32_t kDecBufferBlock = 3;
  constexpr uint32_t kDecDescriptorSet = 34;
  constexpr uint32_t kStorageUniformConstant = 0;
  constexpr uint32_t kStorageUniform = 2;
  constexpr uint32_t kStorageStorageBuffer = 12;

  // Slang targets SPIR-V 1.0, where a Vulkan SSBO is encoded as an
  // OpTypeStruct decorated with BufferBlock + OpTypePointer Uniform.
  // (SPIR-V >= 1.3 would use storage class StorageBuffer directly.)
  // Collect the set of pointer-type result-ids that target a BufferBlock
  // struct so we can tell the SSBO Uniform pointers apart from real UB
  // Uniform pointers below.
  std::vector<uint32_t> bb_struct_ids;
  {
    size_t i = 5;
    while (i < nwords) {
      uint32_t w0 = words[i];
      uint32_t wc = w0 >> 16;
      uint32_t op = w0 & 0xffff;
      if (wc == 0 || i + wc > nwords)
        break;
      if (op == kOpDecorate && wc >= 3 && words[i + 2] == kDecBufferBlock) {
        bb_struct_ids.push_back(words[i + 1]);
      }
      i += wc;
    }
  }
  std::vector<uint32_t> ssbo_ptr_type_ids;
  {
    size_t i = 5;
    while (i < nwords) {
      uint32_t w0 = words[i];
      uint32_t wc = w0 >> 16;
      uint32_t op = w0 & 0xffff;
      if (wc == 0 || i + wc > nwords)
        break;
      if (op == kOpTypePointer && wc >= 4) {
        uint32_t ptr_id = words[i + 1];
        uint32_t storage = words[i + 2];
        uint32_t pointed = words[i + 3];
        if (storage == kStorageUniform) {
          for (uint32_t s : bb_struct_ids) {
            if (s == pointed) {
              ssbo_ptr_type_ids.push_back(ptr_id);
              break;
            }
          }
        }
      }
      i += wc;
    }
  }

  // Map each resource OpVariable's ID to its descriptor-set destination.
  // "kind 0" = texture/sampler/SSBO (UniformConstant, StorageBuffer, or
  //           Uniform-with-BufferBlock SSBO)
  // "kind 1" = uniform block (Uniform pointing at a plain Block struct)
  std::vector<std::pair<uint32_t, int>> id_to_kind;
  id_to_kind.reserve(8);

  size_t i = 5; // skip header
  while (i < nwords) {
    uint32_t w0 = words[i];
    uint32_t wc = w0 >> 16;
    uint32_t op = w0 & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpVariable && wc >= 4) {
      uint32_t res_type = words[i + 1];
      uint32_t id = words[i + 2];
      uint32_t storage = words[i + 3];
      if (storage == kStorageUniformConstant ||
          storage == kStorageStorageBuffer) {
        id_to_kind.push_back({id, 0});
      } else if (storage == kStorageUniform) {
        bool is_ssbo = false;
        for (uint32_t p : ssbo_ptr_type_ids) {
          if (p == res_type) {
            is_ssbo = true;
            break;
          }
        }
        id_to_kind.push_back({id, is_ssbo ? 0 : 1});
      }
    }
    i += wc;
  }
  if (id_to_kind.empty())
    return;

  auto kind_of = [&](uint32_t id) -> int {
    for (auto &p : id_to_kind)
      if (p.first == id)
        return p.second;
    return -1;
  };

  // Resolve target set for (kind, target, stage).
  auto target_set = [&](int kind) -> int {
    if (target == SHADER_TARGET_SOKOL) {
      return (kind == 0) ? 1 : 0; // image=set1, ub=set0
    }
    // SDL_GPU per-stage table:
    if (stage == SpvStage::Vertex) {
      return (kind == 0) ? 0 : 1; // image=set0, ub=set1
    } else if (stage == SpvStage::Fragment) {
      return (kind == 0) ? 2 : 3; // image=set2, ub=set3
    } else {
      // Compute. kind 0 is currently treated as RW storage (set 1) and
      // kind 1 is uniform (set 2). Readonly storage / sampled-texture
      // would belong on set 0 but are not exposed yet.
      return (kind == 0) ? 1 : 2;
    }
  };

  // Pass 2: rewrite DescriptorSet decorations.
  i = 5;
  while (i < nwords) {
    uint32_t w0 = words[i];
    uint32_t wc = w0 >> 16;
    uint32_t op = w0 & 0xffff;
    if (wc == 0 || i + wc > nwords)
      break;
    if (op == kOpDecorate && wc >= 4) {
      uint32_t tgt = words[i + 1];
      uint32_t deco = words[i + 2];
      if (deco == kDecDescriptorSet) {
        int k = kind_of(tgt);
        if (k >= 0) {
          int s = target_set(k);
          if (s >= 0)
            words[i + 3] = (uint32_t)s;
        }
      }
    }
    i += wc;
  }
}

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
void renumber_fs_image_bindings_sdlgpu(ShaderBlob *fs_blob,
                                       ShaderReflection *refl) {
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

  TargetDesc slang_target = {};
  slang_target.format = SLANG_SPIRV;
  slang_target.profile = g_slang.spirv_profile;

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

  // Concatenate VS and FS into a single module. They are kept distinct via
  // their `[shader("vertex")]` / `[shader("fragment")]` attributes. The
  // per-target prelude is prepended so the LUB_TEXTURE2D / LUB_SAMPLE
  // macros expand to the descriptor layout this backend expects.
  const char *prelude = prelude_for_target(target);
  std::string combined;
  combined.reserve(strlen(prelude) + strlen(vs_src) + strlen(fs_src) + 4);
  combined.append(prelude);
  combined.append(vs_src);
  combined.append("\n");
  combined.append(fs_src);

  ComPtr<IBlob> diag;
  IModule *modRaw = session->loadModuleFromSourceString(
      "user", "user.slang", combined.c_str(), diag.writeRef());
  if (!modRaw) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  ComPtr<IModule> module(modRaw);

  ComPtr<IEntryPoint> vsEp, fsEp;
  if (SLANG_FAILED(module->findEntryPointByName("vs_main", vsEp.writeRef())) ||
      !vsEp) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "vs_main entry point not found");
    return false;
  }
  if (SLANG_FAILED(module->findEntryPointByName("fs_main", fsEp.writeRef())) ||
      !fsEp) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "fs_main entry point not found");
    return false;
  }

  IComponentType *components[] = {module.get(), vsEp.get(), fsEp.get()};
  ComPtr<IComponentType> composite;
  if (SLANG_FAILED(session->createCompositeComponentType(
          components, 3, composite.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  ComPtr<IComponentType> linked;
  if (SLANG_FAILED(composite->link(linked.writeRef(), diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  // Get target code: entry 0 is vs (declared first in `components`),
  // entry 1 is fs. With target.format == SLANG_SPIRV the blobs hold
  // SPIR-V binary, not GLSL source.
  ComPtr<IBlob> vsBlob, fsBlob;
  if (SLANG_FAILED(linked->getEntryPointCode(0, 0, vsBlob.writeRef(),
                                             diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }
  if (SLANG_FAILED(linked->getEntryPointCode(1, 0, fsBlob.writeRef(),
                                             diag.writeRef()))) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  // Reflection
  ProgramLayout *programLayout = linked->getLayout(0, diag.writeRef());
  if (!programLayout) {
    copy_diag(diag.get(), err_buf, err_buf_size);
    return false;
  }

  EntryPointReflection *vsRefl = nullptr;
  SlangUInt epc = programLayout->getEntryPointCount();
  for (SlangUInt i = 0; i < epc; ++i) {
    EntryPointReflection *ep = programLayout->getEntryPointByIndex(i);
    if (ep && ep->getStage() == SLANG_STAGE_VERTEX) {
      vsRefl = ep;
      break;
    }
  }
  if (!fill_attrs_from_entry_point(vsRefl, out_refl, err_buf, err_buf_size)) {
    return false;
  }
  fill_global_reflection(programLayout, out_refl);

  // Copy the SPIR-V bytes into caller-owned mutable malloc'd buffers and
  // patch descriptor sets in place.
  size_t vs_size = vsBlob->getBufferSize();
  size_t fs_size = fsBlob->getBufferSize();
  out_vs->spirv = (uint32_t *)malloc(vs_size);
  if (!out_vs->spirv) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (vs blob)");
    return false;
  }
  memcpy(out_vs->spirv, vsBlob->getBufferPointer(), vs_size);
  out_vs->bytes = vs_size;

  out_fs->spirv = (uint32_t *)malloc(fs_size);
  if (!out_fs->spirv) {
    free(out_vs->spirv);
    out_vs->spirv = nullptr;
    out_vs->bytes = 0;
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (fs blob)");
    return false;
  }
  memcpy(out_fs->spirv, fsBlob->getBufferPointer(), fs_size);
  out_fs->bytes = fs_size;

  // patch_spirv_descriptor_sets assigns per-(storage class, stage,
  // target backend) descriptor-set numbers. For sdlgpu fragment stage
  // this places textures+samplers at set 2.
  patch_spirv_descriptor_sets(out_vs->spirv, out_vs->bytes, target,
                              SpvStage::Vertex);
  patch_spirv_descriptor_sets(out_fs->spirv, out_fs->bytes, target,
                              SpvStage::Fragment);

  if (target == SHADER_TARGET_SDLGPU) {
    // Renumber FS image/sampler bindings 0-based per stage. Required
    // because Slang allocates bindings module-wide while SDL_GPU's
    // pipeline layout expects num_samplers=N => bindings 0..N-1.
    // Combined Sampler2D<> source (via the LUB_TEXTURE2D macro
    // prelude) already produces a single OpVariable per texture.
    renumber_fs_image_bindings_sdlgpu(out_fs, out_refl);
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
  slang_target.format = SLANG_SPIRV;
  slang_target.profile = g_slang.spirv_profile;

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
  fill_global_reflection(programLayout, out_refl);

  size_t cs_size = csBlob->getBufferSize();
  out_cs->spirv = (uint32_t *)malloc(cs_size);
  if (!out_cs->spirv) {
    if (err_buf && err_buf_size)
      snprintf(err_buf, err_buf_size, "OOM (cs blob)");
    return false;
  }
  memcpy(out_cs->spirv, csBlob->getBufferPointer(), cs_size);
  out_cs->bytes = cs_size;

  patch_spirv_descriptor_sets(out_cs->spirv, out_cs->bytes, target,
                              SpvStage::Compute);
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
enum SglShaderStage {
  SGL_STAGE_VS = 0,
  SGL_STAGE_FS = 1,
  SGL_STAGE_CS = 2,
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
// lub.js WASM glue, so disable formatting for this region.
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
bool ub_slot_exists(const ShaderReflection *refl, int slot) {
  for (int i = 0; i < refl->ub_count; ++i) {
    if (refl->ubs[i].slot == slot)
      return true;
  }
  return false;
}
bool tex_slot_exists(const ShaderReflection *refl, int img_slot) {
  for (int i = 0; i < refl->tex_count; ++i) {
    if (refl->texs[i].img_slot == img_slot)
      return true;
  }
  return false;
}
// Used by the storage-buffer dedup path in reflect_from_slang_json.
bool sbuf_slot_exists(const ShaderReflection *refl, int slot) {
  for (int i = 0; i < refl->storage_buf_count; ++i) {
    if (refl->storage_bufs[i].slot == slot)
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
  int idx = -1;
  if (f.contains("binding") && f["binding"].is_object() &&
      f["binding"].contains("index")) {
    idx = f["binding"]["index"].get<int>();
  }
  int cc = 0;
  if (f.contains("type"))
    cc = comp_count_of_type_json(f["type"]);
  if (cc <= 0)
    cc = 4;

  ShaderAttr *a = &out->attrs[out->attr_count];
  copy_name_capped(a->name, sizeof(a->name), name);
  a->slot = (idx >= 0) ? idx : out->attr_count;
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
void fill_uniform_block_from_json(const json &p, int slot,
                                  ShaderUniformBlock *u) {
  u->slot = slot;
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
void process_global_parameter(const json &p, ShaderReflection *out) {
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
    if (ub_slot_exists(out, slot))
      return;
    if (out->ub_count >= SGL_MAX_UNIFORM_BLOCKS)
      return;
    fill_uniform_block_from_json(p, slot, &out->ubs[out->ub_count++]);
    return;
  }
  if (tkind == "resource") {
    std::string shape = t->value("baseShape", std::string(""));
    if (shape == "structuredBuffer" || shape == "byteAddressBuffer") {
      if (sbuf_slot_exists(out, slot))
        return;
      if (out->storage_buf_count >= SGL_MAX_STORAGE_BUFS)
        return;
      ShaderStorageBuf *sb = &out->storage_bufs[out->storage_buf_count++];
      copy_name_capped(sb->name, sizeof(sb->name),
                       p.value("name", std::string("")));
      sb->slot = slot;
      // access="readWrite" => writable; absent or "read" => readonly.
      sb->readonly = (t->value("access", std::string("")) != "readWrite");
      return;
    }
    // Texture (baseShape="texture2D"/"texture3D"/...). Sampler usually
    // comes via a separate parameter with type.kind="samplerState", but a
    // combined `Sampler2D<>` source has Slang emit "combined": true and
    // serves both image and sampler from a single descriptor slot.
    if (tex_slot_exists(out, slot))
      return;
    if (out->tex_count >= SGL_MAX_TEXTURES)
      return;
    ShaderTexture *tx = &out->texs[out->tex_count++];
    copy_name_capped(tx->name, sizeof(tx->name),
                     p.value("name", std::string("")));
    tx->img_slot = slot;
    bool combined = t->value("combined", false);
    tx->smp_slot = combined ? slot : -1;
    return;
  }
  if (tkind == "samplerState") {
    // Pair with the next unpaired texture in declaration order. This
    // matches the native (Slang COM API) path's behaviour and works for
    // the typical `Texture2D foo; SamplerState foo_smp;` convention.
    for (int k = 0; k < out->tex_count; ++k) {
      if (out->texs[k].smp_slot < 0) {
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
                             bool is_vertex_stage) {
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
      process_global_parameter(p, out);
    }
    for (const auto &p : j["parameters"]) {
      if (!p.is_object())
        continue;
      const auto *t = p.contains("type") ? &p["type"] : nullptr;
      if (t && t->is_object() &&
          t->value("kind", std::string("")) == "samplerState") {
        process_global_parameter(p, out);
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
      if (!is_vertex_stage || stage != "vertex")
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
  return true;
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
  // sokol-wgpu only on wasm; descriptor-set patching N/A for WGSL. But
  // the LUB_TEXTURE2D / LUB_SAMPLE macros still need expanding so the
  // shader source can be shared with native sokol/sdlgpu builds.
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
  if (!compile_one(vs_with_prelude.c_str(), "vs_main", SGL_STAGE_VS, out_vs,
                   vs_refl_json, err_buf, err_buf_size)) {
    return false;
  }
  if (!compile_one(fs_with_prelude.c_str(), "fs_main", SGL_STAGE_FS, out_fs,
                   fs_refl_json, err_buf, err_buf_size)) {
    free(out_vs->spirv);
    out_vs->spirv = nullptr;
    out_vs->bytes = 0;
    return false;
  }

  // Merge both stages' reflection JSON into the single ShaderReflection
  // struct: VS contributes attrs + UBs + textures, FS contributes its
  // own UBs + textures. reflect_from_slang_json() now dedups by slot so
  // a UB/texture declared in both stages collapses to one entry.
  if (!reflect_from_slang_json(vs_refl_json.c_str(), out_refl,
                               /*is_vertex_stage=*/true) ||
      !reflect_from_slang_json(fs_refl_json.c_str(), out_refl,
                               /*is_vertex_stage=*/false)) {
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
  if (!compile_one(cs_with_prelude.c_str(), "cs_main", SGL_STAGE_CS, out_cs,
                   cs_refl_json, err_buf, err_buf_size)) {
    return false;
  }
  out_refl->is_compute = true;
  out_refl->workgroup[0] = out_refl->workgroup[1] = out_refl->workgroup[2] = 1;
  if (!reflect_from_slang_json(cs_refl_json.c_str(), out_refl,
                               /*is_vertex_stage=*/false)) {
    if (err_buf && err_buf_size) {
      snprintf(err_buf, err_buf_size,
               "slang reflection parse failed (compute)");
    }
    free(out_cs->spirv);
    out_cs->spirv = nullptr;
    out_cs->bytes = 0;
    return false;
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

#endif // __EMSCRIPTEN__
