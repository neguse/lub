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

// Rewrite descriptor-set decorations in a SPIR-V module to match sokol's
// Vulkan backend layout: uniform buffers stay on set 0, but every other
// resource (textures, samplers, storage buffers, storage images, etc.) is
// moved to set 1. Slang emits all globals on set 0 by default, which causes
// VkPipelineLayout / SPIR-V mismatch panics on strict drivers (e.g. lavapipe
// SIGSEGV in samples/03_texture before this patch).
//
// SPIR-V layout reference:
//   header = 5 words, then a stream of instructions where word0 = (wc<<16)|op.
// We do two passes:
//   1. Collect IDs of OpVariable %T %ID UniformConstant or StorageBuffer.
//      These are the resources that belong on set 1.
//   2. For each OpDecorate %ID DescriptorSet 0 referring to one of those IDs,
//      rewrite the literal 0 to 1 in place.
// All other decorations (UB on set 0, bindings, locations, ...) are left
// untouched.
void patch_spirv_descriptor_sets(void *spv, size_t size_bytes) {
    if (!spv || size_bytes < 20) return; // too small to be valid
    uint32_t *words = (uint32_t*)spv;
    size_t nwords = size_bytes / 4;
    if (words[0] != 0x07230203u) return; // not a SPIR-V module

    // Opcodes / decorations / storage-classes we care about.
    constexpr uint32_t kOpVariable     = 59;
    constexpr uint32_t kOpDecorate     = 71;
    constexpr uint32_t kDecDescriptorSet = 34;
    constexpr uint32_t kStorageUniformConstant = 0;
    constexpr uint32_t kStorageStorageBuffer   = 12;

    std::vector<uint32_t> set1_ids;
    set1_ids.reserve(8);

    // Pass 1: collect IDs that should live on set 1.
    size_t i = 5; // skip header
    while (i < nwords) {
        uint32_t w0 = words[i];
        uint32_t wc = w0 >> 16;
        uint32_t op = w0 & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpVariable && wc >= 4) {
            // OpVariable: words[i+1]=ResultType, [i+2]=ResultId, [i+3]=StorageClass
            uint32_t storage = words[i + 3];
            if (storage == kStorageUniformConstant ||
                storage == kStorageStorageBuffer)
            {
                set1_ids.push_back(words[i + 2]);
            }
        }
        i += wc;
    }
    if (set1_ids.empty()) return;

    auto is_set1 = [&](uint32_t id) {
        for (uint32_t v : set1_ids) if (v == id) return true;
        return false;
    };

    // Pass 2: bump DescriptorSet decorations for collected IDs.
    i = 5;
    while (i < nwords) {
        uint32_t w0 = words[i];
        uint32_t wc = w0 >> 16;
        uint32_t op = w0 & 0xffff;
        if (wc == 0 || i + wc > nwords) break;
        if (op == kOpDecorate && wc >= 4) {
            // OpDecorate: words[i+1]=Target, [i+2]=Decoration, [i+3]=Operand0
            uint32_t target = words[i + 1];
            uint32_t deco   = words[i + 2];
            if (deco == kDecDescriptorSet && is_set1(target)) {
                words[i + 3] = 1;
            }
        }
        i += wc;
    }
}

} // anonymous namespace

extern "C" bool shader_compile(
    const char *vs_src, const char *fs_src,
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

    TargetDesc target = {};
    target.format = SLANG_SPIRV;
    target.profile = g_slang.spirv_profile;

    SessionDesc sd = {};
    sd.targets = &target;
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

    patch_spirv_descriptor_sets(out_vs->spirv, out_vs->bytes);
    patch_spirv_descriptor_sets(out_fs->spirv, out_fs->bytes);
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
