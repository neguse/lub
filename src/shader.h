#pragma once
#include <stddef.h>
#include <stdint.h>
#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

#define SGL_MAX_ATTRS 8
#define SGL_MAX_UB_MEMBERS 32
#define SGL_MAX_TEXTURES 8
#define SGL_MAX_UNIFORM_BLOCKS 2
#define SGL_MAX_STORAGE_BUFS 4

typedef struct ShaderAttr {
    char name[32];
    int slot;          // input location
    int comp_count;    // 1..4
    int offset_floats; // within the vertex stride
} ShaderAttr;

typedef struct ShaderUniformMember {
    char name[32];
    int offset_floats;
    int comp_count;    // mat4 = 16, vec3 = 3, vec4 = 4, etc.
} ShaderUniformMember;

typedef struct ShaderUniformBlock {
    char name[32];
    int slot;
    int size_floats;
    int member_count;
    ShaderUniformMember members[SGL_MAX_UB_MEMBERS];
} ShaderUniformBlock;

typedef struct ShaderTexture {
    char name[32];
    int img_slot;
    int smp_slot;
} ShaderTexture;

typedef struct ShaderStorageBuf {
    char name[32];
    int slot;
    bool readonly;
} ShaderStorageBuf;

typedef struct ShaderReflection {
    int attr_count;
    ShaderAttr attrs[SGL_MAX_ATTRS];
    int ub_count;
    ShaderUniformBlock ubs[SGL_MAX_UNIFORM_BLOCKS];
    int tex_count;
    ShaderTexture texs[SGL_MAX_TEXTURES];
    int vertex_stride_floats;
    // Compute-only reflection. is_compute=true for compute shaders compiled via
    // shader_compile_compute; ubs/texs/storage_bufs may still be populated for
    // graphics shaders that use those resources.
    bool is_compute;
    int workgroup[3];                // [numthreads(x,y,z)] from cs entry point
    int storage_buf_count;
    ShaderStorageBuf storage_bufs[SGL_MAX_STORAGE_BUFS];
} ShaderReflection;

// SPIR-V byte blob, owner = caller (free with shader_blob_free).
typedef struct ShaderBlob {
    uint32_t *spirv;
    size_t bytes;
} ShaderBlob;

// Target backend for SPIR-V descriptor-set patching. The two backends use
// different Vulkan descriptor-set layouts (sokol: UB on set 0, samplers on
// set 1; SDL_GPU: per stage, vs textures=0/UB=1, fs textures=2/UB=3) so the
// SPIR-V emitted by Slang has to be rewritten differently for each.
typedef enum ShaderTargetBackend {
    SHADER_TARGET_SOKOL  = 0,
    SHADER_TARGET_SDLGPU = 1,
} ShaderTargetBackend;

bool shader_compile(
    const char *vs_src, const char *fs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_vs, ShaderBlob *out_fs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size);

bool shader_compile_compute(
    const char *cs_src,
    ShaderTargetBackend target,
    ShaderBlob *out_cs,
    ShaderReflection *out_refl,
    char *err_buf, size_t err_buf_size);

void shader_blob_free(ShaderBlob *b);

#ifdef __cplusplus
}
#endif
