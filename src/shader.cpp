// Slang shader compile + reflection -> SPIR-V blob + ShaderReflection
//
// Compiles two source strings (vertex and fragment) written in Slang to
// SPIR-V, and returns the SPIR-V byte blobs plus a small ShaderReflection
// struct to the caller. The actual GPU shader object construction
// (sg_make_shader / SDL_CreateGPUShader / etc.) lives in the backend.
// This file is C++ because the Slang public API uses COM-like C++
// interfaces; the exposed interface (shader.h) is pure C.
#include "shader.h"

#include <slang.h>
#include <slang-com-ptr.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <vector>

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
    // Prefer spirv_1_5; fall back to glsl_450 (which still produces SPIR-V
    // when target.format == SLANG_SPIRV).
    g_slang.spirv_profile = g_slang.g->findProfile("spirv_1_5");
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
enum class SpvStage { Vertex, Fragment };

void patch_spirv_descriptor_sets(void *spv, size_t size_bytes,
                                 ShaderTargetBackend target, SpvStage stage) {
    if (!spv || size_bytes < 20) return; // too small to be valid
    uint32_t *words = (uint32_t*)spv;
    size_t nwords = size_bytes / 4;
    if (words[0] != 0x07230203u) return; // not a SPIR-V module

    // Opcodes / decorations / storage-classes we care about.
    constexpr uint32_t kOpVariable     = 59;
    constexpr uint32_t kOpDecorate     = 71;
    constexpr uint32_t kDecDescriptorSet = 34;
    constexpr uint32_t kStorageUniformConstant = 0;
    constexpr uint32_t kStorageUniform         = 2;
    constexpr uint32_t kStorageStorageBuffer   = 12;

    // Map each resource OpVariable's ID to its descriptor-set destination.
    // "kind 0" = texture/sampler/SSBO (UniformConstant or StorageBuffer)
    // "kind 1" = uniform block (Uniform)
    std::vector<std::pair<uint32_t, int>> id_to_kind;
    id_to_kind.reserve(8);

    size_t i = 5; // skip header
    while (i < nwords) {
        uint32_t w0 = words[i];
        uint32_t wc = w0 >> 16;
        uint32_t op = w0 & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpVariable && wc >= 4) {
            uint32_t storage = words[i + 3];
            uint32_t id      = words[i + 2];
            if (storage == kStorageUniformConstant ||
                storage == kStorageStorageBuffer)
            {
                id_to_kind.push_back({id, 0});
            } else if (storage == kStorageUniform) {
                id_to_kind.push_back({id, 1});
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
        } else {
            return (kind == 0) ? 2 : 3;  // image=set2, ub=set3
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
// We leave the original OpVariable / OpDecorate / OpName instructions
// in place but overwrite them with OpNop so binary length stays the
// same (offset bookkeeping is then trivial).
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
//        - In OpEntryPoint interface list, drop the sampler id (the
//          interface count word stays the same length-wise — we either
//          shrink wordCount or just keep the id; we shrink, then write
//          OpNop in the freed slot to keep total length stable).
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
        if (name_found) {
            for (int t = 0; t < refl->tex_count; ++t) {
                if (strcmp(refl->texs[t].name, tex_name) == 0) {
                    refl->texs[t].smp_slot = refl->texs[t].img_slot;
                    break;
                }
            }
        } else {
            // Fallback: set first texture's smp_slot to img_slot.
            refl->texs[0].smp_slot = refl->texs[0].img_slot;
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

extern "C" void shader_blob_free(ShaderBlob *b) {
    if (!b) return;
    if (b->spirv) {
        free(b->spirv);
        b->spirv = nullptr;
    }
    b->bytes = 0;
}
