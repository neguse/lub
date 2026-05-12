// Slang shader compile + reflection -> SPIR-V blob + ShaderReflection
//
// Compiles two source strings (vertex and fragment) written in Slang to
// SPIR-V, and returns the SPIR-V byte blobs plus a small ShaderReflection
// struct to the caller. The actual GPU shader object construction
// (sg_make_shader / SDL_CreateGPUShader / etc.) lives in the backend.
// This file is C++ because the Slang public API uses COM-like C++
// interfaces; the exposed interface (shader.h) is pure C.
//
// Emscripten: the Slang prebuilt is native-only; Phase 4 will load
// slang-wasm via EM_ASYNC_JS. Until then shader_compile / _compute return
// failure so use_shader keeps the previous (or empty) shader entry. The
// caller already has fallback paths for compile failure (samples log the
// error and continue), so a stub-fail keeps the frame loop alive.
#include "shader.h"

#ifndef __EMSCRIPTEN__
#include <slang.h>
#include <slang-com-ptr.h>
#endif

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

#ifndef __EMSCRIPTEN__
using slang::IGlobalSession;
using slang::ISession;
using slang::IModule;
using slang::IEntryPoint;
using slang::IComponentType;
using slang::IBlob;
using slang::SessionDesc;
using slang::TargetDesc;
using slang::ProgramLayout;
using slang::EntryPointReflection;
using slang::VariableLayoutReflection;
using slang::TypeLayoutReflection;
using slang::TypeReflection;
using Slang::ComPtr;

namespace {

// Lazy global session, reused across compiles.
struct GlobalSlangCtx {
    ComPtr<IGlobalSession> g;
    SlangProfileID spirv_profile = SLANG_PROFILE_UNKNOWN;
};

GlobalSlangCtx g_slang;

bool ensure_global_session() {
    if (g_slang.g) return true;
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
    if (!err_buf || err_buf_size == 0) return;
    if (!diag) {
        snprintf(err_buf, err_buf_size, "(no diagnostic available)");
        return;
    }
    size_t n = diag->getBufferSize();
    if (n >= err_buf_size) n = err_buf_size - 1;
    memcpy(err_buf, diag->getBufferPointer(), n);
    err_buf[n] = '\0';
}

void copy_name(char *dst, size_t cap, const char *src) {
    if (cap == 0) return;
    if (!src) { dst[0] = '\0'; return; }
    size_t n = strlen(src);
    if (n >= cap) n = cap - 1;
    memcpy(dst, src, n);
    dst[n] = '\0';
}

// Map a slang TypeReflection (vector or scalar) to GLSL component count.
int component_count_of(TypeReflection *t) {
    if (!t) return 0;
    auto kind = t->getKind();
    if (kind == TypeReflection::Kind::Scalar) return 1;
    if (kind == TypeReflection::Kind::Vector) return (int)t->getElementCount();
    if (kind == TypeReflection::Kind::Matrix) {
        return (int)(t->getRowCount() * t->getColumnCount());
    }
    return 0;
}

bool fill_attrs_from_entry_point(EntryPointReflection *ep, ShaderReflection *out, char *err, size_t errsz) {
    out->attr_count = 0;
    out->vertex_stride_floats = 0;
    if (!ep) {
        if (err && errsz) snprintf(err, errsz, "no vertex entry point reflection");
        return false;
    }
    unsigned pcount = ep->getParameterCount();
    int offset = 0;
    for (unsigned i = 0; i < pcount; ++i) {
        VariableLayoutReflection *p = ep->getParameterByIndex(i);
        if (!p) continue;
        TypeLayoutReflection *tl = p->getTypeLayout();
        if (!tl) continue;
        TypeReflection *t = tl->getType();
        if (!t) continue;

        // Two cases:
        //  (a) parameter is a struct: iterate its fields as varying inputs.
        //  (b) parameter is a vector/scalar: it's a single varying input.
        auto record_one = [&](const char *name, TypeReflection *vt) -> bool {
            if (out->attr_count >= SGL_MAX_ATTRS) {
                if (err && errsz) snprintf(err, errsz, "too many vertex attributes (>%d)", SGL_MAX_ATTRS);
                return false;
            }
            ShaderAttr *a = &out->attrs[out->attr_count];
            copy_name(a->name, sizeof(a->name), name);
            a->slot = out->attr_count; // input location: assume sequential
            a->comp_count = component_count_of(vt);
            if (a->comp_count <= 0) a->comp_count = 4;
            a->offset_floats = offset;
            offset += a->comp_count;
            out->attr_count++;
            return true;
        };

        if (t->getKind() == TypeReflection::Kind::Struct) {
            unsigned fc = tl->getFieldCount();
            for (unsigned f = 0; f < fc; ++f) {
                VariableLayoutReflection *fl = tl->getFieldByIndex(f);
                if (!fl) continue;
                TypeReflection *ft = fl->getTypeLayout()->getType();
                if (!record_one(fl->getName() ? fl->getName() : "attr", ft)) return false;
            }
        } else {
            if (!record_one(p->getName() ? p->getName() : "attr", t)) return false;
        }
    }
    out->vertex_stride_floats = offset;
    return true;
}

bool fill_uniform_block(VariableLayoutReflection *p, ShaderUniformBlock *ub) {
    copy_name(ub->name, sizeof(ub->name), p->getName());
    ub->slot = (int)p->getBindingIndex();
    ub->size_floats = 0;
    ub->member_count = 0;

    TypeLayoutReflection *tl = p->getTypeLayout();
    if (!tl) return false;
    TypeReflection *t = tl->getType();
    if (!t) return false;

    // For a ConstantBuffer<T>, walk into its element layout.
    TypeLayoutReflection *element = tl;
    if (t->getKind() == TypeReflection::Kind::ConstantBuffer) {
        element = tl->getElementTypeLayout();
        t = element ? element->getType() : nullptr;
    }
    if (!element || !t) return false;

    size_t total_bytes = element->getSize(SLANG_PARAMETER_CATEGORY_UNIFORM);
    ub->size_floats = (int)((total_bytes + 3) / 4);

    if (t->getKind() != TypeReflection::Kind::Struct) return true;

    unsigned fc = element->getFieldCount();
    for (unsigned f = 0; f < fc && ub->member_count < SGL_MAX_UB_MEMBERS; ++f) {
        VariableLayoutReflection *fl = element->getFieldByIndex(f);
        if (!fl) continue;
        ShaderUniformMember *m = &ub->members[ub->member_count++];
        copy_name(m->name, sizeof(m->name), fl->getName());
        size_t off_bytes = fl->getOffset(SLANG_PARAMETER_CATEGORY_UNIFORM);
        m->offset_floats = (int)(off_bytes / 4);
        TypeReflection *mt = fl->getTypeLayout()->getType();
        m->comp_count = component_count_of(mt);
        if (m->comp_count <= 0) m->comp_count = 1;
    }
    return true;
}

bool fill_global_reflection(ProgramLayout *layout, ShaderReflection *out) {
    if (!layout) return false;
    out->ub_count = 0;
    out->tex_count = 0;
    out->storage_buf_count = 0;
    unsigned gpc = layout->getParameterCount();
    for (unsigned i = 0; i < gpc; ++i) {
        VariableLayoutReflection *p = layout->getParameterByIndex(i);
        if (!p) continue;
        SlangParameterCategory cat = (SlangParameterCategory)p->getCategory();
        TypeReflection *t = p->getTypeLayout() ? p->getTypeLayout()->getType() : nullptr;

        // Constant buffers / uniform blocks
        if (cat == SLANG_PARAMETER_CATEGORY_CONSTANT_BUFFER ||
            (t && t->getKind() == TypeReflection::Kind::ConstantBuffer))
        {
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
            SlangResourceShape shape = (SlangResourceShape)
                (t->getResourceShape() & SLANG_RESOURCE_BASE_SHAPE_MASK);
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

        // Textures (SRV) — record as a texture entry; sampler match is filled below.
        if (cat == SLANG_PARAMETER_CATEGORY_SHADER_RESOURCE ||
            (t && t->getKind() == TypeReflection::Kind::Resource))
        {
            if (out->tex_count < SGL_MAX_TEXTURES) {
                ShaderTexture *tx = &out->texs[out->tex_count++];
                copy_name(tx->name, sizeof(tx->name), p->getName());
                tx->img_slot = (int)p->getBindingIndex();
                tx->smp_slot = -1; // resolved next
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
            (t && t->getKind() == TypeReflection::Kind::SamplerState))
        {
            int sidx = (int)p->getBindingIndex();
            int matched = -1;
            for (int k = 0; k < out->tex_count; ++k) {
                if (out->texs[k].smp_slot < 0) { matched = k; break; }
            }
            if (matched >= 0) out->texs[matched].smp_slot = sidx;
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
//     fragment stage  -> set 2: textures/samplers/storage; set 3: uniform buffers
//     compute stage   -> set 0: sampled textures + RO storage textures + RO storage buffers
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
// Compute support is intentionally PoC-narrow: a single RW storage buffer
// (kind 0) + an optional uniform block. Readonly storage buffers and storage
// textures would need an additional kind to map them to SDL_GPU's compute
// set 0 — out of scope here.
enum class SpvStage { Vertex, Fragment, Compute };

void patch_spirv_descriptor_sets(void *spv, size_t size_bytes,
                                 ShaderTargetBackend target, SpvStage stage) {
    if (!spv || size_bytes < 20) return; // too small to be valid
    uint32_t *words = (uint32_t*)spv;
    size_t nwords = size_bytes / 4;
    if (words[0] != 0x07230203u) return; // not a SPIR-V module

    // Opcodes / decorations / storage-classes we care about.
    constexpr uint32_t kOpTypePointer  = 32;
    constexpr uint32_t kOpVariable     = 59;
    constexpr uint32_t kOpDecorate     = 71;
    constexpr uint32_t kDecBufferBlock   = 3;
    constexpr uint32_t kDecDescriptorSet = 34;
    constexpr uint32_t kStorageUniformConstant = 0;
    constexpr uint32_t kStorageUniform         = 2;
    constexpr uint32_t kStorageStorageBuffer   = 12;

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
            if (wc == 0 || i + wc > nwords) break;
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
            if (wc == 0 || i + wc > nwords) break;
            if (op == kOpTypePointer && wc >= 4) {
                uint32_t ptr_id  = words[i + 1];
                uint32_t storage = words[i + 2];
                uint32_t pointed = words[i + 3];
                if (storage == kStorageUniform) {
                    for (uint32_t s : bb_struct_ids) {
                        if (s == pointed) { ssbo_ptr_type_ids.push_back(ptr_id); break; }
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
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpVariable && wc >= 4) {
            uint32_t res_type = words[i + 1];
            uint32_t id       = words[i + 2];
            uint32_t storage  = words[i + 3];
            if (storage == kStorageUniformConstant ||
                storage == kStorageStorageBuffer)
            {
                id_to_kind.push_back({id, 0});
            } else if (storage == kStorageUniform) {
                bool is_ssbo = false;
                for (uint32_t p : ssbo_ptr_type_ids) {
                    if (p == res_type) { is_ssbo = true; break; }
                }
                id_to_kind.push_back({id, is_ssbo ? 0 : 1});
            }
        }
        i += wc;
    }
    if (id_to_kind.empty()) return;

    auto kind_of = [&](uint32_t id) -> int {
        for (auto &p : id_to_kind) if (p.first == id) return p.second;
        return -1;
    };

    // Resolve target set for (kind, target, stage).
    auto target_set = [&](int kind) -> int {
        if (target == SHADER_TARGET_SOKOL) {
            return (kind == 0) ? 1 : 0;  // image=set1, ub=set0
        }
        // SDL_GPU per-stage table:
        if (stage == SpvStage::Vertex) {
            return (kind == 0) ? 0 : 1;  // image=set0, ub=set1
        } else if (stage == SpvStage::Fragment) {
            return (kind == 0) ? 2 : 3;  // image=set2, ub=set3
        } else {
            // Compute. PoC narrow: kind 0 is treated as RW storage (set 1) and
            // kind 1 is uniform (set 2). Readonly storage / sampled-texture
            // would belong on set 0 but are not exercised here.
            return (kind == 0) ? 1 : 2;
        }
    };

    // Pass 2: rewrite DescriptorSet decorations.
    i = 5;
    while (i < nwords) {
        uint32_t w0 = words[i];
        uint32_t wc = w0 >> 16;
        uint32_t op = w0 & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpDecorate && wc >= 4) {
            uint32_t tgt  = words[i + 1];
            uint32_t deco = words[i + 2];
            if (deco == kDecDescriptorSet) {
                int k = kind_of(tgt);
                if (k >= 0) {
                    int s = target_set(k);
                    if (s >= 0) words[i + 3] = (uint32_t)s;
                }
            }
        }
        i += wc;
    }
}

// ---------------------------------------------------------------------------
// SPIR-V combined-image-sampler synthesis for the SDL_GPU backend.
//
// Slang emits HLSL `Texture2D x; SamplerState x_smp;` as two separate
// OpVariables (UniformConstant pointer to OpTypeImage and to OpTypeSampler)
// and a per-call OpSampledImage that combines them. Sokol-gfx + Vulkan
// accepts this layout because it requests separate VK_DESCRIPTOR_TYPE_
// SAMPLED_IMAGE / VK_DESCRIPTOR_TYPE_SAMPLER descriptors.
//
// SDL_GPU's Vulkan path, in contrast, declares `samplers` as
// VK_DESCRIPTOR_TYPE_COMBINED_IMAGE_SAMPLER. Validation then complains
// (VUID-VkGraphicsPipelineCreateInfo-layout-07988) that the SPIR-V's
// separate OpTypeSampler doesn't match the pipeline-layout's
// COMBINED_IMAGE_SAMPLER descriptor.
//
// Fix: rewrite each (texture, sampler) pair into a single OpVariable of
// type "Pointer<UniformConstant, OpTypeSampledImage>", and replace
// each OpSampledImage call site with an OpLoad of that combined variable.
// Implementation: we rebuild the SPIR-V module into a fresh buffer,
// copying each instruction except the ones being deleted (sampler
// OpVariable / OpDecorate / OpName / OpLoad, plus the OpSampledImage
// that fed the texture sample call). In-place edits are applied to:
// OpEntryPoint (drop sampler from interface), the texture's OpVariable /
// OpLoad result-type / sample-call image operand. (An earlier draft
// padded deleted slots with OpNop to keep offsets stable; that approach
// was abandoned in favor of a fresh rebuild.)
//
// Limitations:
//  - Single-pair test path only (sample 03). Multi-pair, image arrays,
//    sampler arrays, comparison samplers etc. are not handled — they'd
//    require additional plumbing. We assume each OpSampledImage's image
//    and sampler operands trace back to OpVariable result ids whose
//    types are exactly OpTypePointer(UniformConstant, OpTypeImage) and
//    OpTypePointer(UniformConstant, OpTypeSampler).
//  - We mutate `out_refl->texs[i].smp_slot` to equal img_slot so the
//    sdlgpu backend's name->slot lookup hits the right combined slot.

// Implementation strategy:
//   1. Scan once to identify the texture variable id, sampler variable id,
//      and the OpSampledImage instruction. Also locate the OpTypeSampledImage
//      that references the image type (Slang already emits one because
//      OpSampledImage uses it).
//   2. Allocate one fresh id (combined pointer-type id). Bound bumps by 1.
//   3. Rebuild the module into a new word vector, applying:
//        - Insert new OpTypePointer(UniformConstant, OpTypeSampledImage)
//          immediately after the OpTypeSampledImage instruction.
//        - Promote OpVariable %tex's type-id operand to the new pointer.
//        - Drop OpName %smp, OpDecorate %smp *, OpVariable %smp.
//        - In OpEntryPoint interface list, drop the sampler id and emit
//          a new header with shrunk wordCount (the rebuild buffer makes
//          the freed slot disappear naturally; no OpNop padding needed).
//        - Drop OpLoad of the sampler variable.
//        - Promote OpLoad of the image variable: change its result-type
//          operand from %img_t to %sampledimage_t.
//        - Drop OpSampledImage (its result id is fed by the image OpLoad
//          directly now); rewrite its consumer OpImageSample* to take the
//          OpLoad's id instead of the OpSampledImage's id.
bool patch_spirv_combined_samplers(ShaderBlob *blob, ShaderReflection *refl) {
    if (!blob || !blob->spirv || blob->bytes < 20) return true;
    uint32_t *src = blob->spirv;
    size_t nwords = blob->bytes / 4;
    if (src[0] != 0x07230203u) return true;

    constexpr uint32_t kOpName                   = 5;
    constexpr uint32_t kOpEntryPoint             = 15;
    constexpr uint32_t kOpTypeImage              = 25;
    constexpr uint32_t kOpTypeSampler            = 26;
    constexpr uint32_t kOpTypeSampledImage       = 27;
    constexpr uint32_t kOpTypePointer            = 32;
    constexpr uint32_t kOpVariable               = 59;
    constexpr uint32_t kOpLoad                   = 61;
    constexpr uint32_t kOpDecorate               = 71;
    constexpr uint32_t kOpSampledImage           = 86;
    constexpr uint32_t kOpImageSampleImplicitLod = 87;
    constexpr uint32_t kOpImageSampleExplicitLod = 88;
    constexpr uint32_t kStorageUniformConstant   = 0;

    // ---- Pass 1: classify ids by type/storage class. -----------------------
    // For every OpTypePointer record (id, storage, pointee).
    // For every OpTypeImage / OpTypeSampler / OpTypeSampledImage record their id.
    struct PtrTy { uint32_t id, storage, pointee; };
    std::vector<PtrTy> ptr_types;
    std::vector<uint32_t> image_type_ids;
    std::vector<uint32_t> sampler_type_ids;
    // Map image_type_id -> sampledImage_type_id (the OpTypeSampledImage that
    // wraps it); used so we can reuse Slang's existing OpTypeSampledImage.
    std::vector<std::pair<uint32_t,uint32_t>> img_to_sampled;

    auto is_image_type = [&](uint32_t id) {
        for (uint32_t t : image_type_ids) if (t == id) return true;
        return false;
    };
    auto is_sampler_type = [&](uint32_t id) {
        for (uint32_t t : sampler_type_ids) if (t == id) return true;
        return false;
    };
    auto sampled_for = [&](uint32_t img_t) -> uint32_t {
        for (auto &p : img_to_sampled) if (p.first == img_t) return p.second;
        return 0;
    };
    auto ptr_pointee = [&](uint32_t ptr_id) -> uint32_t {
        for (auto &p : ptr_types) if (p.id == ptr_id) return p.pointee;
        return 0;
    };

    for (size_t i = 5; i < nwords; ) {
        uint32_t hdr = src[i];
        uint32_t wc = hdr >> 16;
        uint32_t op = hdr & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpTypeImage && wc >= 2) {
            image_type_ids.push_back(src[i + 1]);
        } else if (op == kOpTypeSampler && wc >= 2) {
            sampler_type_ids.push_back(src[i + 1]);
        } else if (op == kOpTypeSampledImage && wc >= 3) {
            uint32_t result = src[i + 1];
            uint32_t img_t  = src[i + 2];
            img_to_sampled.push_back({img_t, result});
        } else if (op == kOpTypePointer && wc >= 4) {
            ptr_types.push_back({src[i + 1], src[i + 2], src[i + 3]});
        }
        i += wc;
    }

    // ---- Pass 2: identify variables that are texture / sampler. -----------
    // var_id -> kind (0 = texture, 1 = sampler), pointer_type_id, pointee_type_id
    struct VarInfo { uint32_t id; int kind; uint32_t ptr_t; uint32_t pointee; };
    std::vector<VarInfo> vars;
    for (size_t i = 5; i < nwords; ) {
        uint32_t hdr = src[i];
        uint32_t wc = hdr >> 16;
        uint32_t op = hdr & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpVariable && wc >= 4) {
            uint32_t ptr_t = src[i + 1];
            uint32_t id    = src[i + 2];
            uint32_t sc    = src[i + 3];
            if (sc == kStorageUniformConstant) {
                uint32_t pe = ptr_pointee(ptr_t);
                int kind = -1;
                if (is_image_type(pe))   kind = 0;
                if (is_sampler_type(pe)) kind = 1;
                if (kind >= 0) vars.push_back({id, kind, ptr_t, pe});
            }
        }
        i += wc;
    }

    // ---- Pass 3: find OpSampledImage instructions; trace operands. --------
    struct Pair {
        uint32_t tex_var, smp_var;     // OpVariable result-ids
        uint32_t sampled_type;          // OpTypeSampledImage id used at the call
        uint32_t tex_load_result;       // result-id of the OpLoad of the texture
        uint32_t sampled_image_result;  // result-id of the OpSampledImage
    };
    // Scratch maps for OpLoad: result_id -> (pointer_var_id).
    struct LoadInfo { uint32_t result; uint32_t pointer; };
    std::vector<LoadInfo> loads;
    for (size_t i = 5; i < nwords; ) {
        uint32_t hdr = src[i];
        uint32_t wc = hdr >> 16;
        uint32_t op = hdr & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpLoad && wc >= 4) {
            loads.push_back({src[i + 2], src[i + 3]});
        }
        i += wc;
    }
    auto load_pointer = [&](uint32_t result) -> uint32_t {
        for (auto &l : loads) if (l.result == result) return l.pointer;
        return 0;
    };
    auto var_kind = [&](uint32_t id) -> int {
        for (auto &v : vars) if (v.id == id) return v.kind;
        return -1;
    };
    auto var_pointee = [&](uint32_t id) -> uint32_t {
        for (auto &v : vars) if (v.id == id) return v.pointee;
        return 0;
    };

    std::vector<Pair> pairs;
    for (size_t i = 5; i < nwords; ) {
        uint32_t hdr = src[i];
        uint32_t wc = hdr >> 16;
        uint32_t op = hdr & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpSampledImage && wc == 5) {
            uint32_t result    = src[i + 2];
            uint32_t img_op    = src[i + 3];
            uint32_t smp_op    = src[i + 4];
            uint32_t img_var   = load_pointer(img_op);
            uint32_t smp_var   = load_pointer(smp_op);
            if (img_var && smp_var &&
                var_kind(img_var) == 0 && var_kind(smp_var) == 1)
            {
                uint32_t img_t = var_pointee(img_var);
                uint32_t st    = sampled_for(img_t);
                if (st == 0) {
                    // Should not happen for Slang output.
                    i += wc;
                    continue;
                }
                // Dedup: only one Pair per (tex_var, smp_var).
                bool dup = false;
                for (auto &p : pairs) {
                    if (p.tex_var == img_var && p.smp_var == smp_var) {
                        dup = true; break;
                    }
                }
                if (!dup) {
                    pairs.push_back({img_var, smp_var, st, img_op, result});
                }
                (void)img_t;
            }
        }
        i += wc;
    }
    if (pairs.empty()) return true;

    // For now we only support a single pair (matches sample 03's diffuse).
    if (pairs.size() != 1) {
        fprintf(stderr,
                "[shader] combined-sampler patcher: %zu pairs found, only 1 supported; skipping\n",
                pairs.size());
        return true;
    }
    const Pair &P = pairs[0];

    // Allocate a new id for the new pointer type.
    uint32_t new_ptr_id = src[3]; // bound = next free id
    src[3] = new_ptr_id + 1;

    // ---- Rebuild the module into a fresh vector. ---------------------------
    std::vector<uint32_t> dst;
    dst.reserve(nwords + 8);
    // Header: copy as-is (already updated bound above).
    for (int k = 0; k < 5; ++k) dst.push_back(src[k]);

    bool inserted_new_ptr = false;

    // Safety bail: scan for any consumer of P.sampled_image_result whose
    // opcode is NOT in the rewrite-handled set. We currently only rewrite
    // OpImageSampleImplicitLod / OpImageSampleExplicitLod. If Slang ever
    // emits e.g. OpImageSampleProj*, OpImage*Dref*, OpImageGather,
    // OpImageDrefGather, or OpImageQueryLod, deleting the OpSampledImage
    // would leave a dangling reference and produce invalid SPIR-V.
    {
        const uint16_t handled_sample_opcodes[] = {
            (uint16_t)kOpImageSampleImplicitLod,  // 87
            (uint16_t)kOpImageSampleExplicitLod,  // 88
        };
        for (size_t i = 5; i < nwords; ) {
            uint32_t hdr = src[i];
            uint32_t wc = hdr >> 16;
            uint32_t op = hdr & 0xffff;
            if (wc == 0 || i + wc > nwords) break;
            for (uint32_t k = 3; k < wc; ++k) {
                if (src[i + k] == P.sampled_image_result) {
                    bool ok = false;
                    for (size_t s = 0; s < sizeof(handled_sample_opcodes)/sizeof(*handled_sample_opcodes); ++s) {
                        if (op == handled_sample_opcodes[s]) { ok = true; break; }
                    }
                    if (!ok) {
                        fprintf(stderr,
                                "[shader] combined-sampler patcher bailing -- unhandled SampledImage consumer opcode=%u\n",
                                op);
                        return true;
                    }
                }
            }
            i += wc;
        }
    }

    for (size_t i = 5; i < nwords; ) {
        uint32_t hdr = src[i];
        uint32_t wc = hdr >> 16;
        uint32_t op = hdr & 0xffff;
        if (wc == 0 || i + wc > nwords) {
            // Malformed tail; copy verbatim and stop transforming.
            for (size_t k = i; k < nwords; ++k) dst.push_back(src[k]);
            break;
        }
        bool emit = true;

        if (op == kOpEntryPoint) {
            // Layout: hdr, exec_model, fn_id, name(LiteralString), interface_ids...
            // Find start of interface list: skip name string.
            size_t name_start = i + 3;
            size_t name_end = name_start;
            while (name_end < i + wc) {
                uint32_t w = src[name_end];
                ++name_end;
                if ((w & 0x000000ff) == 0 || (w & 0x0000ff00) == 0 ||
                    (w & 0x00ff0000) == 0 || (w & 0xff000000) == 0) {
                    // Contains the null terminator byte: end of string.
                    break;
                }
            }
            // Build a new interface list, dropping the sampler var id.
            std::vector<uint32_t> iface;
            for (size_t k = name_end; k < i + wc; ++k) {
                if (src[k] == P.smp_var) continue;
                iface.push_back(src[k]);
            }
            uint32_t new_wc = (uint32_t)((name_end - i) + iface.size());
            // Emit new OpEntryPoint header + (exec model, fn id, name) + interface.
            dst.push_back((new_wc << 16) | kOpEntryPoint);
            for (size_t k = i + 1; k < name_end; ++k) dst.push_back(src[k]);
            for (uint32_t id : iface) dst.push_back(id);
            emit = false;
        } else if (op == kOpName && wc >= 2) {
            uint32_t target = src[i + 1];
            if (target == P.smp_var) emit = false;
            // Also drop the OpName for the (now-deleted) OpSampledImage
            // result — its id no longer has a definition.
            if (target == P.sampled_image_result) emit = false;
        } else if (op == kOpDecorate && wc >= 4) {
            uint32_t target = src[i + 1];
            if (target == P.smp_var) emit = false;
            // Renumber the texture's Binding decoration to 0. Slang allocates
            // bindings across both UBs and SRVs (so a shader with a UB and a
            // Texture2D gets binding=0 / binding=1), but the SDL_GPU pipeline
            // layout exposes num_samplers=1 = a single binding at slot 0.
            // Single-pair only; multi-pair support (separate task) will need
            // to assign each pair to its index in declaration order.
            constexpr uint32_t kDecBinding = 33;
            if (target == P.tex_var && wc >= 4 && src[i + 2] == kDecBinding) {
                dst.push_back(hdr);
                dst.push_back(target);
                dst.push_back(kDecBinding);
                dst.push_back(0u);
                emit = false;
            }
        } else if (op == kOpTypeSampledImage && wc >= 3) {
            // Copy as-is. If this is the OpTypeSampledImage we're going
            // to point at, immediately append the new OpTypePointer so
            // that the OpTypePointer's pointee dependency is satisfied.
            for (size_t k = i; k < i + wc; ++k) dst.push_back(src[k]);
            uint32_t this_id = src[i + 1];
            if (!inserted_new_ptr && this_id == P.sampled_type) {
                // OpTypePointer = wc 4: hdr, result, storage, type
                dst.push_back((4u << 16) | kOpTypePointer);
                dst.push_back(new_ptr_id);
                dst.push_back(kStorageUniformConstant);
                dst.push_back(P.sampled_type);
                inserted_new_ptr = true;
            }
            emit = false;
        } else if (op == kOpVariable && wc >= 4) {
            uint32_t var_id = src[i + 2];
            if (var_id == P.smp_var) {
                emit = false; // drop sampler variable
            } else if (var_id == P.tex_var) {
                // Promote pointer-type operand to the new combined pointer.
                dst.push_back(hdr);
                dst.push_back(new_ptr_id);   // new result-type-id
                dst.push_back(var_id);
                dst.push_back(src[i + 3]);   // storage class
                for (uint32_t k = 4; k < wc; ++k) dst.push_back(src[i + k]);
                emit = false;
            }
        } else if (op == kOpLoad && wc >= 4) {
            uint32_t result   = src[i + 2];
            uint32_t pointer  = src[i + 3];
            if (pointer == P.smp_var) {
                emit = false; // drop sampler load
            } else if (pointer == P.tex_var) {
                // Promote result-type from image -> sampledImage.
                dst.push_back(hdr);
                dst.push_back(P.sampled_type);
                dst.push_back(result);
                dst.push_back(pointer);
                for (uint32_t k = 4; k < wc; ++k) dst.push_back(src[i + k]);
                emit = false;
            }
        } else if (op == kOpSampledImage && wc == 5) {
            uint32_t result = src[i + 2];
            if (result == P.sampled_image_result) emit = false;
        } else if ((op == kOpImageSampleImplicitLod ||
                    op == kOpImageSampleExplicitLod) && wc >= 5) {
            // Operand layout: hdr, result_t, result, sampled_image, coord, [opts]
            // If sampled_image == old OpSampledImage result, redirect to
            // the (now type-promoted) image OpLoad result.
            if (src[i + 3] == P.sampled_image_result) {
                dst.push_back(hdr);
                dst.push_back(src[i + 1]);
                dst.push_back(src[i + 2]);
                dst.push_back(P.tex_load_result);
                for (uint32_t k = 4; k < wc; ++k) dst.push_back(src[i + k]);
                emit = false;
            }
        }

        if (emit) {
            for (size_t k = i; k < i + wc; ++k) dst.push_back(src[k]);
        }
        i += wc;
    }

    // ---- Replace blob contents. -------------------------------------------
    size_t new_bytes = dst.size() * 4;
    uint32_t *nb = (uint32_t*)malloc(new_bytes);
    if (!nb) return false;
    memcpy(nb, dst.data(), new_bytes);
    free(blob->spirv);
    blob->spirv = nb;
    blob->bytes = new_bytes;

    // ---- Sync reflection. -------------------------------------------------
    // Both img_slot and smp_slot now refer to the combined sampler at
    // (set 2, binding = original tex img_slot). Find the matching entry
    // in refl by looking up the image-variable's binding via OpDecorate.
    // Easier: just set every smp_slot = img_slot for textures whose name
    // matches a tex_var we patched. Since we only support 1 pair, set the
    // first texture entry's smp_slot to img_slot.
    if (refl && refl->tex_count > 0) {
        // Find the tex name corresponding to P.tex_var by scanning OpName
        // in the patched binary. (Safer than indexing by 0.)
        char tex_name[64] = {0};
        bool name_found = false;
        for (size_t i = 5; i < dst.size(); ) {
            uint32_t hdr = dst[i];
            uint32_t wc = hdr >> 16;
            uint32_t op = hdr & 0xffff;
            if (wc == 0 || i + wc > dst.size()) break;
            if (op == kOpName && wc >= 2 && dst[i + 1] == P.tex_var) {
                size_t bytes_avail = (wc - 2) * 4;
                size_t cp = bytes_avail < sizeof(tex_name) - 1 ? bytes_avail : sizeof(tex_name) - 1;
                memcpy(tex_name, &dst[i + 2], cp);
                tex_name[cp] = '\0';
                name_found = true;
                break;
            }
            i += wc;
        }
        // We rewrote the texture's Binding decoration to 0 above. Mirror that
        // in the reflection so the apply_bindings name->slot lookup hits the
        // right combined-sampler slot.
        if (name_found) {
            for (int t = 0; t < refl->tex_count; ++t) {
                if (strcmp(refl->texs[t].name, tex_name) == 0) {
                    refl->texs[t].img_slot = 0;
                    refl->texs[t].smp_slot = 0;
                    break;
                }
            }
        } else {
            refl->texs[0].img_slot = 0;
            refl->texs[0].smp_slot = 0;
        }
    }
    return true;
}

} // anonymous namespace

extern "C" bool shader_compile(
    const char *vs_src, const char *fs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_vs, ShaderBlob *out_fs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    if (out_vs) { out_vs->spirv = nullptr; out_vs->bytes = 0; }
    if (out_fs) { out_fs->spirv = nullptr; out_fs->bytes = 0; }
    if (out_refl) memset(out_refl, 0, sizeof(*out_refl));

    if (!ensure_global_session()) {
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "createGlobalSession failed");
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
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "createSession failed");
        return false;
    }

    // Concatenate VS and FS into a single module. They are kept distinct via
    // their `[shader("vertex")]` / `[shader("fragment")]` attributes.
    std::string combined;
    combined.reserve(strlen(vs_src) + strlen(fs_src) + 4);
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
    if (SLANG_FAILED(module->findEntryPointByName("vs_main", vsEp.writeRef())) || !vsEp) {
        if (err_buf && err_buf_size)
            snprintf(err_buf, err_buf_size, "vs_main entry point not found");
        return false;
    }
    if (SLANG_FAILED(module->findEntryPointByName("fs_main", fsEp.writeRef())) || !fsEp) {
        if (err_buf && err_buf_size)
            snprintf(err_buf, err_buf_size, "fs_main entry point not found");
        return false;
    }

    IComponentType *components[] = { module.get(), vsEp.get(), fsEp.get() };
    ComPtr<IComponentType> composite;
    if (SLANG_FAILED(session->createCompositeComponentType(components, 3,
            composite.writeRef(), diag.writeRef()))) {
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
    if (SLANG_FAILED(linked->getEntryPointCode(0, 0, vsBlob.writeRef(), diag.writeRef()))) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }
    if (SLANG_FAILED(linked->getEntryPointCode(1, 0, fsBlob.writeRef(), diag.writeRef()))) {
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
    out_vs->spirv = (uint32_t*)malloc(vs_size);
    if (!out_vs->spirv) {
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "OOM (vs blob)");
        return false;
    }
    memcpy(out_vs->spirv, vsBlob->getBufferPointer(), vs_size);
    out_vs->bytes = vs_size;

    out_fs->spirv = (uint32_t*)malloc(fs_size);
    if (!out_fs->spirv) {
        free(out_vs->spirv); out_vs->spirv = nullptr; out_vs->bytes = 0;
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "OOM (fs blob)");
        return false;
    }
    memcpy(out_fs->spirv, fsBlob->getBufferPointer(), fs_size);
    out_fs->bytes = fs_size;

    // Patcher order matters:
    //   1. patch_spirv_descriptor_sets — assigns descriptor-set numbers
    //      per (storage class, stage, target backend). For sdlgpu fragment
    //      stage this places textures+samplers at set 2.
    //   2. patch_spirv_combined_samplers (sdlgpu only) — fuses the
    //      texture+sampler pair into a single combined-image-sampler
    //      OpVariable, inheriting the descriptor set/binding the previous
    //      pass wrote on the texture variable. Reversing this order would
    //      mean the combined variable inherits unassigned/wrong sets.
    patch_spirv_descriptor_sets(out_vs->spirv, out_vs->bytes, target, SpvStage::Vertex);
    patch_spirv_descriptor_sets(out_fs->spirv, out_fs->bytes, target, SpvStage::Fragment);

    if (target == SHADER_TARGET_SDLGPU) {
        // Slang emits separate Texture2D + SamplerState as separate
        // OpVariables, but SDL_GPU's Vulkan layout demands COMBINED_IMAGE_
        // SAMPLER. Rewrite the FS SPIR-V to fuse the pair into a single
        // sampled-image variable. Sampling-only (textures without samplers)
        // and uniform buffers are unaffected.
        patch_spirv_combined_samplers(out_fs, out_refl);
    }
    return true;
}

extern "C" bool shader_compile_compute(
    const char *cs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_cs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    if (out_cs)  { out_cs->spirv = nullptr; out_cs->bytes = 0; }
    if (out_refl) memset(out_refl, 0, sizeof(*out_refl));

    if (!ensure_global_session()) {
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "createGlobalSession failed");
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
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "createSession failed");
        return false;
    }

    ComPtr<IBlob> diag;
    IModule *modRaw = session->loadModuleFromSourceString(
        "user_cs", "user_cs.slang", cs_src, diag.writeRef());
    if (!modRaw) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }
    ComPtr<IModule> module(modRaw);

    ComPtr<IEntryPoint> csEp;
    if (SLANG_FAILED(module->findEntryPointByName("cs_main", csEp.writeRef())) || !csEp) {
        if (err_buf && err_buf_size)
            snprintf(err_buf, err_buf_size, "cs_main entry point not found");
        return false;
    }

    IComponentType *components[] = { module.get(), csEp.get() };
    ComPtr<IComponentType> composite;
    if (SLANG_FAILED(session->createCompositeComponentType(components, 2,
            composite.writeRef(), diag.writeRef()))) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }
    ComPtr<IComponentType> linked;
    if (SLANG_FAILED(composite->link(linked.writeRef(), diag.writeRef()))) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }

    ComPtr<IBlob> csBlob;
    if (SLANG_FAILED(linked->getEntryPointCode(0, 0, csBlob.writeRef(), diag.writeRef()))) {
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
        if (!ep || ep->getStage() != SLANG_STAGE_COMPUTE) continue;
        SlangUInt sizes[3] = {1, 1, 1};
        ep->getComputeThreadGroupSize(3, sizes);
        out_refl->workgroup[0] = (int)sizes[0];
        out_refl->workgroup[1] = (int)sizes[1];
        out_refl->workgroup[2] = (int)sizes[2];
        break;
    }
    fill_global_reflection(programLayout, out_refl);

    size_t cs_size = csBlob->getBufferSize();
    out_cs->spirv = (uint32_t*)malloc(cs_size);
    if (!out_cs->spirv) {
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "OOM (cs blob)");
        return false;
    }
    memcpy(out_cs->spirv, csBlob->getBufferPointer(), cs_size);
    out_cs->bytes = cs_size;

    patch_spirv_descriptor_sets(out_cs->spirv, out_cs->bytes, target, SpvStage::Compute);
    return true;
}

extern "C" void shader_blob_free(ShaderBlob *b) {
    if (!b) return;
    if (b->spirv) {
        free(b->spirv);
        b->spirv = nullptr;
    }
    b->bytes = 0;
}

#else  // __EMSCRIPTEN__

// -------------------------------------------------------------------------
// Emscripten Slang bridge.
//
// We delegate Slang compilation to the JS side via EM_ASYNC_JS. The JS
// glue (`window.slangCompile`, defined in Phase 6 in
// web/playground/slang-bridge.ts) loads `@shader-slang/slang-wasm`, runs
// Slang in-page, and returns `{wgsl, reflectJson}` (or `{error}`).
//
// File layout (this block):
//   1. EM_ASYNC_JS shim `sglua_slang_compile_js` — single async call point.
//   2. `reflect_from_slang_json` — populate ShaderReflection from Slang's
//      reflection JSON. Phase 4 lands a near-empty implementation; Phase 6
//      will iterate based on observed Slang output.
//   3. `shader_compile` / `shader_compile_compute` — drive (1) twice/once
//      and (2) per blob, returning WGSL bytes into ShaderBlob.spirv.

#include <emscripten.h>
#include "../third_party/nlohmann/json.hpp"

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
EM_ASYNC_JS(char*, sglua_slang_compile_js,
            (const char* src, const char* entry, int stage),
{
    const srcStr   = UTF8ToString(src);
    const entryStr = UTF8ToString(entry);
    const packError = (msg) => {
        const errMsg = '\x02' + msg;
        const len = lengthBytesUTF8(errMsg) + 1;
        const ptr = _malloc(len);
        if (!ptr) return 0;
        stringToUTF8(errMsg, ptr, len);
        return ptr;
    };
    if (typeof window === 'undefined' ||
        typeof window.slangCompile !== 'function') {
        console.error('[sglua] window.slangCompile not yet defined ' +
                      "(Phase 6 hasn't wired up slang-wasm). entry=" + entryStr);
        return packError('slang-wasm not loaded yet (Phase 6)');
    }
    try {
        const result = await window.slangCompile(srcStr, entryStr, stage);
        if (!result || result.error) {
            const msg = (result && result.error)
                ? result.error
                : 'slang compile returned no result';
            console.error('[sglua] slang compile error:', msg);
            return packError(msg);
        }
        // Pack {wgsl, reflectJson} into a single \x01-separated UTF-8 string.
        const blob = result.wgsl + '\x01' + (result.reflectJson || '{}');
        const len = lengthBytesUTF8(blob) + 1;
        const ptr = _malloc(len);
        if (!ptr) return 0;
        stringToUTF8(blob, ptr, len);
        return ptr;
    } catch (e) {
        const msg = (e && e.message) ? e.message : String(e);
        console.error('[sglua] slangCompile threw:', msg);
        return packError(msg);
    }
});

namespace {

using json = nlohmann::json;

void copy_name_capped(char *dst, size_t cap, const std::string &src) {
    if (cap == 0) return;
    size_t n = src.size();
    if (n >= cap) n = cap - 1;
    memcpy(dst, src.data(), n);
    dst[n] = '\0';
}

// Split "wgsl\x01reflectJson" on the first 0x01 byte. Returns false if no sep.
bool split_blob(const char *blob, std::string &out_wgsl, std::string &out_refl_json) {
    if (!blob) return false;
    const char *sep = strchr(blob, '\x01');
    if (!sep) return false;
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
bool ub_slot_exists(const ShaderReflection* refl, int slot) {
    for (int i = 0; i < refl->ub_count; ++i) {
        if (refl->ubs[i].slot == slot) return true;
    }
    return false;
}
bool tex_slot_exists(const ShaderReflection* refl, int img_slot) {
    for (int i = 0; i < refl->tex_count; ++i) {
        if (refl->texs[i].img_slot == img_slot) return true;
    }
    return false;
}
// Unused until Phase 6 wires storage-buffer extraction in reflect_from_slang_json.
[[maybe_unused]] bool sbuf_slot_exists(const ShaderReflection* refl, int slot) {
    for (int i = 0; i < refl->storage_buf_count; ++i) {
        if (refl->storage_bufs[i].slot == slot) return true;
    }
    return false;
}

// Populate ShaderReflection from a Slang `--reflection-json`-style document.
//
// **Phase 4 status: stub.** The exact JSON schema Slang emits via
// IComponentType::getLayout()->toJson()-equivalent in the wasm build hasn't
// been observed against real output yet (Phase 6 will iterate). For now we
// do a best-effort, defensive walk: if we find an obvious "parameters" or
// "entryPoints" array we try to extract attribute / uniform / texture
// names. Missing/unknown fields are silently skipped — the goal is to
// return true so the bridge call succeeds and the rest of the pipeline can
// be exercised end-to-end. Sample-by-sample correctness will be added when
// Phase 6 produces real JSON to diff against.
//
// TODO(Phase 6): replace with a schema-aware mapping once we have ground
// truth from `@shader-slang/slang-wasm`. Specifically map:
//   * parameters[].binding.{kind,index}    -> attr.slot / ub.slot / tex.{img,smp}_slot
//   * parameters[].type.{kind,elementType} -> attr.comp_count / ub.members[].comp_count
//   * entryPoints[].threadGroupSize        -> workgroup[3] for compute
//   * parameters[].type.kind == "resource" -> texs / storage_bufs
bool reflect_from_slang_json(const char *json_text, ShaderReflection *out,
                             bool is_vertex_stage) {
    if (!out) return false;
    if (!json_text || !*json_text) return true; // empty JSON -> nothing to merge

    json j = json::parse(json_text, nullptr, false);
    if (j.is_discarded()) {
        fprintf(stderr, "[sglua] reflect_from_slang_json: parse failed\n");
        return false;
    }

    static bool warned_once = false;
    if (!warned_once) {
        fprintf(stderr,
                "[sglua] reflect_from_slang_json: Phase 4 stub mapping "
                "(field set is incomplete; Phase 6 will flesh this out).\n");
        warned_once = true;
    }

    // Best-effort: walk a top-level "parameters" array if present and pull
    // out anything that smells like a vertex input, uniform block, or texture.
    // The exact schema is TBD; the code below is intentionally tolerant.
    if (j.contains("parameters") && j["parameters"].is_array()) {
        for (const auto &p : j["parameters"]) {
            if (!p.is_object()) continue;
            std::string name = p.value("name", std::string(""));
            std::string kind;
            if (p.contains("binding") && p["binding"].is_object()) {
                kind = p["binding"].value("kind", std::string(""));
            }
            int index = -1;
            if (p.contains("binding") && p["binding"].is_object() &&
                p["binding"].contains("index")) {
                index = p["binding"]["index"].get<int>();
            }

            if (is_vertex_stage && kind == "varyingInput" &&
                out->attr_count < SGL_MAX_ATTRS) {
                ShaderAttr *a = &out->attrs[out->attr_count];
                copy_name_capped(a->name, sizeof(a->name), name);
                a->slot = (index >= 0) ? index : out->attr_count;
                a->comp_count = 4; // TODO(Phase 6): read from type
                a->offset_floats = out->vertex_stride_floats;
                out->vertex_stride_floats += a->comp_count;
                out->attr_count++;
            } else if (kind == "uniform" || kind == "constantBuffer") {
                int slot = (index >= 0) ? index : 0;
                // Cross-stage dedup: same UB declared in both VS and FS
                // collapses to one entry.
                if (!ub_slot_exists(out, slot) &&
                    out->ub_count < SGL_MAX_UNIFORM_BLOCKS) {
                    ShaderUniformBlock *u = &out->ubs[out->ub_count++];
                    copy_name_capped(u->name, sizeof(u->name), name);
                    u->slot = slot;
                    u->size_floats = 0; // TODO(Phase 6): infer from type layout
                    u->member_count = 0;
                }
            } else if (kind == "descriptorTableSlot" || kind == "shaderResource") {
                int slot = (index >= 0) ? index : 0;
                // Cross-stage dedup: same texture/sampler at same image slot.
                if (!tex_slot_exists(out, slot) &&
                    out->tex_count < SGL_MAX_TEXTURES) {
                    ShaderTexture *tx = &out->texs[out->tex_count++];
                    copy_name_capped(tx->name, sizeof(tx->name), name);
                    tx->img_slot = slot;
                    tx->smp_slot = slot;
                }
            }
            // samplerState / storageBuffer / etc. omitted — Phase 6.
            // (When storage buffers land, dedup with sbuf_slot_exists().)
        }
    }

    // Compute thread group size, if present in an entryPoints[] node.
    if (j.contains("entryPoints") && j["entryPoints"].is_array()) {
        for (const auto &ep : j["entryPoints"]) {
            if (!ep.is_object()) continue;
            std::string stage = ep.value("stage", std::string(""));
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
    return true;
}

// Common driver: call the JS bridge once, split, and copy WGSL bytes into
// `out_blob`. Reflection JSON is returned via `out_refl_json` for the caller
// to merge into ShaderReflection.
bool compile_one(const char *src, const char *entry, int stage,
                 ShaderBlob *out_blob, std::string &out_refl_json,
                 char *err_buf, size_t err_buf_size) {
    char *blob = sglua_slang_compile_js(src, entry, stage);
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
            snprintf(err_buf, err_buf_size,
                     "slang(%s) returned malformed blob", entry);
        }
        return false;
    }
    // Stash WGSL source bytes into out_blob->spirv. The field is misnamed on
    // wasm (it's WGSL text, not SPIR-V binary) but it's the same opaque byte
    // container the backend will consume in Phase 6.
    size_t n = wgsl.size();
    out_blob->spirv = (uint32_t*)malloc(n + 1);
    if (!out_blob->spirv) {
        if (err_buf && err_buf_size) {
            snprintf(err_buf, err_buf_size, "OOM copying WGSL (%zu bytes)", n);
        }
        return false;
    }
    memcpy(out_blob->spirv, wgsl.data(), n);
    ((char*)out_blob->spirv)[n] = '\0';
    out_blob->bytes = n;
    return true;
}

} // anonymous namespace

extern "C" bool shader_compile(
    const char *vs_src, const char *fs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_vs, ShaderBlob *out_fs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    (void)target; // sokol-wgpu only on wasm; descriptor-set patching N/A for WGSL.
    if (out_vs) { out_vs->spirv = nullptr; out_vs->bytes = 0; }
    if (out_fs) { out_fs->spirv = nullptr; out_fs->bytes = 0; }
    if (out_refl) memset(out_refl, 0, sizeof(*out_refl));

    std::string vs_refl_json, fs_refl_json;
    if (!compile_one(vs_src, "vs_main", SGL_STAGE_VS, out_vs, vs_refl_json,
                     err_buf, err_buf_size)) {
        return false;
    }
    if (!compile_one(fs_src, "fs_main", SGL_STAGE_FS, out_fs, fs_refl_json,
                     err_buf, err_buf_size)) {
        free(out_vs->spirv); out_vs->spirv = nullptr; out_vs->bytes = 0;
        return false;
    }

    // Merge both stages' reflection JSON into the single ShaderReflection
    // struct: VS contributes attrs + UBs + textures, FS contributes its
    // own UBs + textures. reflect_from_slang_json() now dedups by slot so
    // a UB/texture declared in both stages collapses to one entry.
    if (!reflect_from_slang_json(vs_refl_json.c_str(), out_refl, /*is_vertex_stage=*/true) ||
        !reflect_from_slang_json(fs_refl_json.c_str(), out_refl, /*is_vertex_stage=*/false))
    {
        if (err_buf && err_buf_size) {
            snprintf(err_buf, err_buf_size, "slang reflection parse failed");
        }
        free(out_vs->spirv); out_vs->spirv = nullptr; out_vs->bytes = 0;
        free(out_fs->spirv); out_fs->spirv = nullptr; out_fs->bytes = 0;
        return false;
    }
    return true;
}

extern "C" bool shader_compile_compute(
    const char *cs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_cs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    (void)target;
    if (out_cs)   { out_cs->spirv = nullptr; out_cs->bytes = 0; }
    if (out_refl) memset(out_refl, 0, sizeof(*out_refl));

    std::string cs_refl_json;
    if (!compile_one(cs_src, "cs_main", SGL_STAGE_CS, out_cs, cs_refl_json,
                     err_buf, err_buf_size)) {
        return false;
    }
    out_refl->is_compute = true;
    out_refl->workgroup[0] = out_refl->workgroup[1] = out_refl->workgroup[2] = 1;
    if (!reflect_from_slang_json(cs_refl_json.c_str(), out_refl, /*is_vertex_stage=*/false)) {
        if (err_buf && err_buf_size) {
            snprintf(err_buf, err_buf_size, "slang reflection parse failed (compute)");
        }
        free(out_cs->spirv); out_cs->spirv = nullptr; out_cs->bytes = 0;
        return false;
    }
    return true;
}

extern "C" void shader_blob_free(ShaderBlob *b) {
    if (!b) return;
    if (b->spirv) {
        free(b->spirv);
        b->spirv = nullptr;
    }
    b->bytes = 0;
}

#endif  // __EMSCRIPTEN__
