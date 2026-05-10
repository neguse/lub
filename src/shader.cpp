// Slang shader compile + reflection -> sg_shader
//
// Compiles two source strings (vertex and fragment) written in Slang to GLSL,
// builds an sg_shader_desc with attribute/uniform/texture reflection, and
// hands the resulting sg_shader handle plus a small ShaderReflection struct
// back to the caller. This file is C++ because the Slang public API uses
// COM-like C++ interfaces; the exposed interface (shader.h) is pure C.
#include "shader.h"

#include <slang.h>
#include <slang-com-ptr.h>

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <string>
#include <regex>
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
    SlangProfileID glsl_profile = SLANG_PROFILE_UNKNOWN;
};

GlobalSlangCtx g_slang;

bool ensure_global_session() {
    if (g_slang.g) return true;
    if (SLANG_FAILED(slang::createGlobalSession(g_slang.g.writeRef()))) {
        return false;
    }
    g_slang.glsl_profile = g_slang.g->findProfile("glsl_330");
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

// Slang's GLSL backend always emits `#version 450` regardless of the profile we
// requested. Rewrite that to `#version 330` so sokol_gfx's GL 3.3 backend
// accepts the source. The PoC shaders only use features common to both
// versions (layout qualifiers, basic types, std140 uniform blocks).
//
// Slang also unconditionally emits `layout(column_major) buffer;` which is a
// GLSL 4.30+ construct (SSBO declarations didn't exist before that). The PoC
// doesn't use SSBOs, so we strip that line entirely. Without this strip, the
// driver's GLSL frontend rejects the shader at GL_SHADER_COMPILATION_FAILED on
// first use (sg_make_shader defers compilation, so the failure surfaces on
// sg_apply_pipeline rather than at create time).
std::string downversion_glsl(const char *src, size_t n) {
    std::string s(src, n);
    const char *needle = "#version 450";
    size_t pos = s.find(needle);
    if (pos != std::string::npos) {
        // Replace `#version 450` with `#version 330` plus extension enables.
        // Slang emits:
        //  - `layout(location = N)` on inter-stage varyings, which requires
        //    GL_ARB_separate_shader_objects on GLSL 3.30.
        //  - `layout(binding = N)` on samplers/UBOs, which requires
        //    GL_ARB_shading_language_420pack on GLSL 3.30.
        const char *replacement =
            "#version 330\n"
            "#extension GL_ARB_separate_shader_objects : enable\n"
            "#extension GL_ARB_shading_language_420pack : enable";
        s.replace(pos, strlen(needle), replacement);
    }
    const char *buf_needle = "layout(column_major) buffer;";
    size_t bpos = s.find(buf_needle);
    if (bpos != std::string::npos) {
        // Replace with whitespace of the same length so reported line numbers
        // in driver diagnostics still line up.
        s.replace(bpos, strlen(buf_needle), std::string(strlen(buf_needle), ' '));
    }

    // GLSL 3.30 doesn't support separate texture / sampler opaque types
    // (`texture2D` / `sampler`) — those came in 4.20 + Vulkan-flavored GLSL.
    // Slang emits Vulkan-style separate textures + `sampler2D(tex, smp)`
    // construction. Rewrite the pattern back to traditional combined samplers
    // so GL 3.3 accepts the source:
    //
    //   uniform texture2D NAME;            ->  uniform sampler2D NAME;
    //   uniform sampler  NAME;             ->  (stripped to whitespace)
    //   sampler2D(<texname>, <smpname>)    ->  <texname>
    //
    // The drop of the standalone sampler is safe because every sampler usage
    // is wrapped in a `sampler2D(tex, smp)` call (which we collapse to just
    // the texture), so nothing else references the standalone sampler.
    s = std::regex_replace(s, std::regex(R"(\buniform\s+texture2D\b)"), "uniform sampler2D");
    // Strip the standalone sampler declaration. Slang emits it across two
    // lines in the form `layout(binding = N)\nuniform sampler NAME;`.
    s = std::regex_replace(
        s,
        std::regex(R"(layout\s*\(\s*binding\s*=\s*\d+\s*\)\s*\n\s*uniform\s+sampler\s+\w+_\d+\s*;)"),
        "");
    s = std::regex_replace(s, std::regex(R"(sampler2D\s*\(\s*([A-Za-z_][A-Za-z0-9_]*)\s*,\s*[A-Za-z_][A-Za-z0-9_]*\s*\))"), "$1");

    // Flatten std140 uniform blocks into plain `uniform` declarations.
    //
    // sokol's GL backend doesn't actually use UBOs (no glBindBufferRange /
    // glGetUniformBlockIndex anywhere) — it issues individual glUniform* calls
    // per member at sg_apply_uniforms time, looking each member up via
    // glGetUniformLocation(). That call returns -1 for any name inside a real
    // uniform block, so blocks must be flattened to plain uniforms.
    //
    // Slang emits, e.g.:
    //   layout(binding = 0)
    //   layout(std140) uniform block_Uniforms_0 { mat4x4 mvp_0; ... } u_0;
    //   ... mul(u_0.mvp_0, ...) ...
    //
    // We rewrite the block to:
    //   uniform mat4x4 mvp_0;
    //   ... mul(mvp_0, ...) ...
    //
    // The `layout(column_major) uniform;` default qualifier line emitted near
    // the top is harmless once no real blocks remain.
    {
        // Match the whole block declaration. The (?s) flag isn't supported in
        // libstdc++'s default ECMAScript regex; use [\s\S] for "any char".
        std::regex block_re(
            R"(layout\s*\(\s*binding\s*=\s*\d+\s*\)\s*\n\s*layout\s*\(\s*std140\s*\)\s*uniform\s+\w+\s*\{([\s\S]*?)\}\s*(\w+)\s*;)");
        std::smatch m;
        std::string out;
        std::string::const_iterator search_start = s.cbegin();
        std::vector<std::string> inst_names;
        while (std::regex_search(search_start, s.cend(), m, block_re)) {
            out.append(search_start, m[0].first);
            std::string body = m[1].str();   // text between { ... }
            std::string inst = m[2].str();   // instance name, e.g., "u_0"
            inst_names.push_back(inst);
            // Convert each line of the body (e.g. "    mat4x4 mvp_0;") into
            // `uniform <type> <name>;`. We just keep semicolon-separated decls.
            // Strip leading whitespace per stmt, prefix `uniform `.
            std::string flat;
            size_t pos = 0;
            while (pos < body.size()) {
                size_t semi = body.find(';', pos);
                if (semi == std::string::npos) break;
                std::string stmt = body.substr(pos, semi - pos);
                // trim
                size_t a = stmt.find_first_not_of(" \t\r\n");
                size_t b = stmt.find_last_not_of(" \t\r\n");
                if (a != std::string::npos && b != std::string::npos) {
                    flat += "uniform ";
                    flat += stmt.substr(a, b - a + 1);
                    flat += ";\n";
                }
                pos = semi + 1;
            }
            out += flat;
            search_start = m[0].second;
        }
        out.append(search_start, s.cend());
        s = std::move(out);

        // Strip `<inst>.` prefix from usages.
        for (const auto& inst : inst_names) {
            std::regex usage_re("\\b" + inst + "\\s*\\.");
            s = std::regex_replace(s, usage_re, "");
        }
    }

    return s;
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

sg_uniform_type guess_uniform_type(int comp_count) {
    switch (comp_count) {
        case 1:  return SG_UNIFORMTYPE_FLOAT;
        case 2:  return SG_UNIFORMTYPE_FLOAT2;
        case 3:  return SG_UNIFORMTYPE_FLOAT3;
        case 4:  return SG_UNIFORMTYPE_FLOAT4;
        case 16: return SG_UNIFORMTYPE_MAT4;
        default: return SG_UNIFORMTYPE_FLOAT4;
    }
}

} // anonymous namespace

extern "C" bool shader_compile_and_create(
    const char *vs_src, const char *fs_src,
    sg_shader *out_shader, ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size)
{
    if (out_shader) out_shader->id = 0;
    if (out_refl) memset(out_refl, 0, sizeof(*out_refl));

    if (!ensure_global_session()) {
        if (err_buf && err_buf_size) snprintf(err_buf, err_buf_size, "createGlobalSession failed");
        return false;
    }

    TargetDesc target = {};
    target.format = SLANG_GLSL;
    target.profile = g_slang.glsl_profile;

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
    // entry 1 is fs.
    ComPtr<IBlob> vsBlob, fsBlob;
    if (SLANG_FAILED(linked->getEntryPointCode(0, 0, vsBlob.writeRef(), diag.writeRef()))) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }
    if (SLANG_FAILED(linked->getEntryPointCode(1, 0, fsBlob.writeRef(), diag.writeRef()))) {
        copy_diag(diag.get(), err_buf, err_buf_size);
        return false;
    }

    std::string vs_glsl = downversion_glsl(
        (const char*)vsBlob->getBufferPointer(), vsBlob->getBufferSize());
    std::string fs_glsl = downversion_glsl(
        (const char*)fsBlob->getBufferPointer(), fsBlob->getBufferSize());

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

    // Build sg_shader_desc.
    //
    // Lifetime contract: the std::string locals/vectors below (vs_glsl, fs_glsl,
    // attr_names, ub_member_names, tex_pair_names) own the C-string buffers
    // referenced by sg_shader_desc fields (vertex_func.source, attrs[].glsl_name,
    // uniform_blocks[].glsl_uniforms[].glsl_name, texture_sampler_pairs[].glsl_name,
    // etc.). sg_make_shader copies these strings during the call, so the local
    // std::strings can safely go out of scope after it returns. DO NOT return or
    // branch out of this section between filling the desc and calling
    // sg_make_shader, or the c_str() pointers will dangle.
    sg_shader_desc desc = {};
    desc.vertex_func.source = vs_glsl.c_str();
    desc.vertex_func.entry  = "main"; // GLSL output uses `main`
    desc.fragment_func.source = fs_glsl.c_str();
    desc.fragment_func.entry  = "main";

    // Vertex attributes — Slang's GLSL emitter names vertex inputs
    // `i_<field>_0` for struct fields, with explicit `layout(location=N)`
    // qualifiers (so the location matches the order Slang declared them).
    // We populate glsl_name for safety on backends that need it.
    std::string attr_names[SGL_MAX_ATTRS];
    for (int i = 0; i < out_refl->attr_count && i < SG_MAX_VERTEX_ATTRIBUTES; ++i) {
        char tmp[64];
        snprintf(tmp, sizeof(tmp), "i_%s_0", out_refl->attrs[i].name);
        attr_names[i] = tmp;
        desc.attrs[i].glsl_name = attr_names[i].c_str();
        desc.attrs[i].base_type = SG_SHADERATTRBASETYPE_FLOAT;
    }

    // Uniform blocks.
    std::string ub_member_names[SGL_MAX_UNIFORM_BLOCKS][SG_MAX_UNIFORMBLOCK_MEMBERS];
    for (int b = 0; b < out_refl->ub_count && b < SGL_MAX_UNIFORM_BLOCKS; ++b) {
        ShaderUniformBlock *u = &out_refl->ubs[b];
        int slot = u->slot;
        if (slot < 0 || slot >= SG_MAX_UNIFORMBLOCK_BINDSLOTS) continue;
        sg_shader_uniform_block *dst = &desc.uniform_blocks[slot];
        // TODO(Task 11+): determine UB stage per parameter via Slang reflection.
        // PoC assumption: vertex shader is the only stage that uses uniform blocks.
        // This will break the moment a fragment-stage UB is introduced.
        dst->stage = SG_SHADERSTAGE_VERTEX;
        dst->size = (uint32_t)(u->size_floats * 4);
        dst->layout = SG_UNIFORMLAYOUT_STD140;
        for (int m = 0; m < u->member_count && m < SG_MAX_UNIFORMBLOCK_MEMBERS; ++m) {
            // Slang appends `_0` to identifiers in its GLSL output, and we flatten
            // the uniform block in downversion_glsl, so the lookup name is `<name>_0`.
            ub_member_names[b][m] = std::string(u->members[m].name) + "_0";
            dst->glsl_uniforms[m].type = guess_uniform_type(u->members[m].comp_count);
            dst->glsl_uniforms[m].array_count = 1;
            dst->glsl_uniforms[m].glsl_name = ub_member_names[b][m].c_str();
        }
    }

    // Textures + samplers + texture-sampler pairs (PoC: stage=FRAGMENT).
    std::string tex_pair_names[SGL_MAX_TEXTURES];
    for (int i = 0; i < out_refl->tex_count && i < SGL_MAX_TEXTURES; ++i) {
        ShaderTexture *tx = &out_refl->texs[i];
        int img_slot = tx->img_slot;
        int smp_slot = tx->smp_slot;
        if (img_slot < 0 || img_slot >= SG_MAX_VIEW_BINDSLOTS) continue;

        sg_shader_view *view = &desc.views[img_slot];
        view->texture.stage = SG_SHADERSTAGE_FRAGMENT;
        view->texture.image_type = SG_IMAGETYPE_2D;
        view->texture.sample_type = SG_IMAGESAMPLETYPE_FLOAT;

        if (smp_slot >= 0 && smp_slot < SG_MAX_SAMPLER_BINDSLOTS) {
            sg_shader_sampler *smp = &desc.samplers[smp_slot];
            smp->stage = SG_SHADERSTAGE_FRAGMENT;
            smp->sampler_type = SG_SAMPLERTYPE_FILTERING;
        }
        if (i < SG_MAX_TEXTURE_SAMPLER_PAIRS) {
            sg_shader_texture_sampler_pair *pair = &desc.texture_sampler_pairs[i];
            pair->stage = SG_SHADERSTAGE_FRAGMENT;
            pair->view_slot = (uint8_t)img_slot;
            pair->sampler_slot = (uint8_t)(smp_slot >= 0 ? smp_slot : 0);
            // Slang's GLSL emitter appends `_0` to texture names (same as
            // vertex inputs), so the GLSL identifier is `<name>_0`. sokol_gfx
            // looks this up in the linked program when binding image-samplers
            // via the GL backend.
            char tmp[64];
            snprintf(tmp, sizeof(tmp), "%s_0", tx->name);
            tex_pair_names[i] = tmp;
            pair->glsl_name = tex_pair_names[i].c_str();
        }
    }

    sg_shader sh = sg_make_shader(&desc);
    if (sh.id == 0) {
        if (err_buf && err_buf_size)
            snprintf(err_buf, err_buf_size, "sg_make_shader failed (see sokol log)");
        return false;
    }
    *out_shader = sh;
    return true;
}
