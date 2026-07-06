// Slang -> DXIL smoke: shader_compile / shader_compile_compute with
// SHADER_TARGET_DX12 must produce signed DXIL containers and reflection whose
// slots are HLSL register indices. No D3D12 device involved; this only
// exercises the compiler path (dxcompiler.dll must be loadable, see the DXC
// fetch in CMakeLists.txt).
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

static int is_dxil_container(const ShaderBlob *b) {
  if (!b->spirv || b->bytes < 4)
    return 0;
  const char *m = (const char *)b->spirv;
  return m[0] == 'D' && m[1] == 'X' && m[2] == 'B' && m[3] == 'C';
}

static void test_triangle(void) {
  const char *vs = "struct VSIn { float3 pos : POSITION; };\n"
                   "struct VSOut { float4 pos : SV_Position; };\n"
                   "[shader(\"vertex\")] VSOut vs_main(VSIn i) {\n"
                   "  VSOut o; o.pos = float4(i.pos, 1.0); return o; }\n";
  const char *fs = "[shader(\"fragment\")] float4 fs_main() : SV_Target {\n"
                   "  return float4(1.0, 0.5, 0.0, 1.0); }\n";
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  if (!shader_compile(vs, fs, SHADER_TARGET_DX12, &vsb, &fsb, &refl, err,
                      sizeof(err))) {
    CHECK(0, "triangle: compile failed: %s", err);
    return;
  }
  CHECK(is_dxil_container(&vsb), "triangle: vs blob is not a DXIL container");
  CHECK(is_dxil_container(&fsb), "triangle: fs blob is not a DXIL container");
  CHECK(refl.attr_count == 1, "triangle: attr_count %d != 1", refl.attr_count);
  CHECK(refl.attrs[0].comp_count == 3, "triangle: pos comp_count %d != 3",
        refl.attrs[0].comp_count);
  CHECK(refl.vertex_stride_floats == 3, "triangle: stride %d != 3",
        refl.vertex_stride_floats);
  printf("PASS: triangle (vs %zu bytes, fs %zu bytes)\n", vsb.bytes, fsb.bytes);
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
}

static void test_uniforms_and_texture(void) {
  const char *vs =
      "cbuffer VSParams { float4x4 mvp; };\n"
      "struct VSIn { float3 pos : POSITION; float2 uv : TEXCOORD0; };\n"
      "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD0; };\n"
      "[shader(\"vertex\")] VSOut vs_main(VSIn i) {\n"
      "  VSOut o; o.pos = mul(mvp, float4(i.pos, 1.0)); o.uv = i.uv;\n"
      "  return o; }\n";
  const char *fs =
      "cbuffer FSParams { float4 tint; };\n"
      "LUB_TEXTURE2D(diffuse);\n"
      "struct FSIn { float2 uv : TEXCOORD0; };\n"
      "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
      "  return LUB_SAMPLE(diffuse, i.uv) * tint; }\n";
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  if (!shader_compile(vs, fs, SHADER_TARGET_DX12, &vsb, &fsb, &refl, err,
                      sizeof(err))) {
    CHECK(0, "ub+tex: compile failed: %s", err);
    return;
  }
  CHECK(is_dxil_container(&vsb), "ub+tex: vs blob is not a DXIL container");
  CHECK(is_dxil_container(&fsb), "ub+tex: fs blob is not a DXIL container");
  CHECK(refl.ub_count == 2, "ub+tex: ub_count %d != 2", refl.ub_count);
  int vs_ub = -1, fs_ub = -1;
  for (int i = 0; i < refl.ub_count; i++) {
    if (refl.ubs[i].stage == SGL_STAGE_VERTEX)
      vs_ub = i;
    if (refl.ubs[i].stage == SGL_STAGE_FRAGMENT)
      fs_ub = i;
  }
  CHECK(vs_ub >= 0, "ub+tex: no VS uniform block");
  CHECK(fs_ub >= 0, "ub+tex: no FS uniform block");
  if (vs_ub >= 0) {
    CHECK(refl.ubs[vs_ub].size_floats == 16, "ub+tex: VS ub size %d != 16",
          refl.ubs[vs_ub].size_floats);
    printf("  VS ub '%s' -> b%d\n", refl.ubs[vs_ub].name, refl.ubs[vs_ub].slot);
  }
  if (fs_ub >= 0) {
    printf("  FS ub '%s' -> b%d\n", refl.ubs[fs_ub].name, refl.ubs[fs_ub].slot);
  }
  CHECK(refl.tex_count == 1, "ub+tex: tex_count %d != 1", refl.tex_count);
  if (refl.tex_count == 1) {
    CHECK(refl.texs[0].img_slot >= 0, "ub+tex: img_slot %d < 0",
          refl.texs[0].img_slot);
    CHECK(refl.texs[0].smp_slot >= 0, "ub+tex: smp_slot %d < 0",
          refl.texs[0].smp_slot);
    printf("  FS tex '%s' -> t%d s%d\n", refl.texs[0].name,
           refl.texs[0].img_slot, refl.texs[0].smp_slot);
  }
  printf("PASS: ub+tex\n");
  shader_blob_free(&vsb);
  shader_blob_free(&fsb);
}

static void test_compute(void) {
  const char *cs = "cbuffer Params { uint count; };\n"
                   "RWStructuredBuffer<float> vals;\n"
                   "[shader(\"compute\")][numthreads(64, 1, 1)]\n"
                   "void cs_main(uint3 tid : SV_DispatchThreadID) {\n"
                   "  if (tid.x < count) vals[tid.x] = vals[tid.x] * 2.0; }\n";
  ShaderBlob csb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  if (!shader_compile_compute(cs, SHADER_TARGET_DX12, &csb, &refl, err,
                              sizeof(err))) {
    CHECK(0, "compute: compile failed: %s", err);
    return;
  }
  CHECK(is_dxil_container(&csb), "compute: cs blob is not a DXIL container");
  CHECK(refl.is_compute, "compute: is_compute not set");
  CHECK(refl.workgroup[0] == 64, "compute: workgroup[0] %d != 64",
        refl.workgroup[0]);
  CHECK(refl.storage_buf_count == 1, "compute: storage_buf_count %d != 1",
        refl.storage_buf_count);
  if (refl.storage_buf_count == 1) {
    CHECK(!refl.storage_bufs[0].readonly, "compute: RW buf marked readonly");
    printf("  CS sbuf '%s' -> u%d\n", refl.storage_bufs[0].name,
           refl.storage_bufs[0].slot);
  }
  printf("PASS: compute (cs %zu bytes)\n", csb.bytes);
  shader_blob_free(&csb);
}

int main(void) {
  test_triangle();
  test_uniforms_and_texture();
  test_compute();
  if (g_failures > 0) {
    printf("%d check(s) failed\n", g_failures);
    return 1;
  }
  printf("All shader dx12 smoke tests passed\n");
  return 0;
}
