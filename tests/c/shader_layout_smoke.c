// Buffer layout smoke: StructuredBuffer<T> element structs must have the same
// layout on every target, so shader_compile rejects a struct whose std430
// layout differs from tight packing and accepts one that is already aligned.
// Runs on the SDL_GPU (SPIR-V) target, where std430 applies, and on Apple
// also on the Metal target, which must give the same verdicts and emit the
// packed twin struct that makes the MSL layout tight. It also checks that
// separately declared textures and samplers pair up, and that shapes lub
// can't bind fail the compile instead of reaching the GPU.
#include "../../src/shader.h"
#include <stdio.h>
#include <string.h>

static int g_failures = 0;

#define CHECK(cond, ...)                                                       \
  do {                                                                         \
    if (!(cond)) {                                                             \
      printf("FAIL: " __VA_ARGS__);                                            \
      printf("\n");                                                            \
      g_failures++;                                                            \
    }                                                                          \
  } while (0)

static const char *kFs =
    "[shader(\"fragment\")] float4 fs_main() : SV_Target {\n"
    "  return float4(1.0, 0.5, 0.0, 1.0); }\n";

static const char *vs_with(const char *members, char *buf, size_t cap) {
  snprintf(
      buf, cap,
      "struct V { %s };\n"
      "StructuredBuffer<V> verts;\n"
      "struct VSOut { float4 pos : SV_Position; };\n"
      "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
      "  VSOut o; o.pos = float4(verts[vid].pos.xy, 0.0, 1.0); return o; }\n",
      members);
  return buf;
}

static ShaderTargetBackend g_target = SHADER_TARGET_SDLGPU;

static void expect(const char *label, const char *members, int should_pass,
                   int stride, const char *err_needle) {
  char vs[1024];
  vs_with(members, vs, sizeof(vs));
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok =
      shader_compile(vs, kFs, g_target, &vsb, &fsb, &refl, err, sizeof(err));
  if (should_pass) {
    CHECK(ok, "%s: compile failed: %s", label, err);
    if (ok) {
      CHECK(refl.storage_buf_count == 1, "%s: storage_buf_count %d != 1", label,
            refl.storage_buf_count);
      CHECK(refl.storage_bufs[0].elem_stride == stride, "%s: stride %d != %d",
            label, refl.storage_bufs[0].elem_stride, stride);
      if (g_target == SHADER_TARGET_METAL && strstr(members, "float3"))
        CHECK(strstr((const char *)vsb.spirv, "packed_float3 pos") != NULL,
              "%s: MSL lacks the packed twin struct", label);
    }
  } else {
    CHECK(!ok, "%s: compile should have failed", label);
    CHECK(strstr(err, "buffer layout") != NULL,
          "%s: error lacks 'buffer layout': %s", label, err);
    CHECK(strstr(err, err_needle) != NULL, "%s: error lacks '%s': %s", label,
          err_needle, err);
  }
  if (ok) {
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
  }
  printf("%s: %s%s%s\n", ok == should_pass ? "PASS" : "FAIL", label,
         ok ? "" : " -> ", ok ? "" : err);
}

static void run_target(ShaderTargetBackend target) {
  g_target = target;
  expect("float2 pair", "float2 pos; float2 uv;", 1, 16, NULL);
  expect("padded float3", "float3 pos; float pad; float2 uv; float2 pad2;", 1,
         32, NULL);
  expect("float3 then float2", "float3 pos; float2 uv;", 0, 0, "uv");
  expect("size not multiple of 16", "float3 pos; float pad; float2 uv;", 0, 0,
         "multiple of 16");
  expect("float3 array", "float3 p[2]; float2 pos;", 0, 0, "stride");
}

// Texture/sampler pairing and the SPIR-V descriptor layout check.
static const char *kQuadVs =
    "struct VSOut { float2 uv : TEXCOORD0; float4 pos : SV_Position; };\n"
    "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
    "  float2 p = float2((vid << 1) & 2, vid & 2);\n"
    "  VSOut o; o.uv = p; o.pos = float4(p * 2.0 - 1.0, 0.0, 1.0);\n"
    "  return o; }\n";

static void expect_fs(const char *label, const char *decls, const char *body,
                      const char *err_needle) {
  char fs[1024];
  snprintf(fs, sizeof(fs),
           "%s\nstruct FSIn { float2 uv : TEXCOORD0; };\n"
           "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
           "  return %s; }\n",
           decls, body);
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok = shader_compile(kQuadVs, fs, g_target, &vsb, &fsb, &refl, err,
                          sizeof(err));
  if (!err_needle) {
    CHECK(ok, "%s: compile failed: %s", label, err);
    for (int i = 0; ok && i < refl.tex_count; i++) {
      char want[40];
      snprintf(want, sizeof(want), "%s_sampler", refl.texs[i].name);
      CHECK(refl.texs[i].smp_slot >= 0, "%s: texture %s has no sampler", label,
            refl.texs[i].name);
      CHECK(!strstr(decls, "SamplerState") ||
                strcmp(refl.texs[i].smp_name, want) == 0,
            "%s: texture %s paired with '%s', want '%s'", label,
            refl.texs[i].name, refl.texs[i].smp_name, want);
    }
  } else {
    CHECK(!ok, "%s: compile should have failed", label);
    CHECK(strstr(err, err_needle) != NULL, "%s: error lacks '%s': %s", label,
          err_needle, err);
  }
  if (ok) {
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
  }
  printf("%s: %s%s%s\n", ok == !err_needle ? "PASS" : "FAIL", label,
         ok ? "" : " -> ", ok ? "" : err);
}

static void run_texture_target(ShaderTargetBackend target) {
  g_target = target;
  expect_fs("macro texture", "LUB_TEXTURE2D(a);", "LUB_SAMPLE(a, i.uv)", NULL);
  expect_fs("separate sampler",
            "Texture2D a; SamplerState a_sampler;\n"
            "Texture2D b; SamplerState b_sampler;",
            "a.Sample(a_sampler, i.uv) + b.Sample(b_sampler, i.uv)", NULL);
  expect_fs("shared sampler", "Texture2D a; Texture2D b; SamplerState s;",
            "a.Sample(s, i.uv) + b.Sample(s, i.uv)", "LUB_TEXTURE2D");
  expect_fs("sampler without texture",
            "Texture2D a; SamplerState a_sampler; SamplerState extra;",
            "a.Sample(a_sampler, i.uv) + a.Sample(extra, i.uv)", "'extra'");
  // A global outside a cbuffer lands in Slang's own uniform block, which
  // lub's SPIR-V layout has no place for.
  if (target == SHADER_TARGET_SDLGPU)
    expect_fs("loose global", "float4 tint;", "tint", "descriptor layout");
}

int main(void) {
  run_target(SHADER_TARGET_SDLGPU);
  run_texture_target(SHADER_TARGET_SDLGPU);
#ifdef __APPLE__
  run_target(SHADER_TARGET_METAL);
  run_texture_target(SHADER_TARGET_METAL);
#endif
  if (g_failures) {
    printf("%d failure(s)\n", g_failures);
    return 1;
  }
  return 0;
}
