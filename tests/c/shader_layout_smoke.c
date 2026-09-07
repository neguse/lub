// Buffer layout smoke: StructuredBuffer<T> element structs must have the same
// layout on every target, so shader_compile rejects a struct whose std430
// layout differs from tight packing and accepts one that is already aligned.
// Runs on the SDL_GPU (SPIR-V) target, where std430 applies.
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

static void expect(const char *label, const char *members, int should_pass,
                   int stride, const char *err_needle) {
  char vs[1024];
  vs_with(members, vs, sizeof(vs));
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok = shader_compile(vs, kFs, SHADER_TARGET_SDLGPU, &vsb, &fsb, &refl, err,
                          sizeof(err));
  if (should_pass) {
    CHECK(ok, "%s: compile failed: %s", label, err);
    if (ok) {
      CHECK(refl.storage_buf_count == 1, "%s: storage_buf_count %d != 1", label,
            refl.storage_buf_count);
      CHECK(refl.storage_bufs[0].elem_stride == stride, "%s: stride %d != %d",
            label, refl.storage_bufs[0].elem_stride, stride);
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

int main(void) {
  expect("float2 pair", "float2 pos; float2 uv;", 1, 16, NULL);
  expect("padded float3", "float3 pos; float pad; float2 uv; float2 pad2;", 1,
         32, NULL);
  expect("float3 then float2", "float3 pos; float2 uv;", 0, 0, "uv");
  expect("size not multiple of 16", "float3 pos; float pad; float2 uv;", 0, 0,
         "multiple of 16");
  expect("float3 array", "float3 p[2]; float2 pos;", 0, 0, "stride");
  if (g_failures) {
    printf("%d failure(s)\n", g_failures);
    return 1;
  }
  return 0;
}
