// Buffer layout smoke: StructuredBuffer<T> element structs must have the same
// layout on every target, so shader_compile rejects a struct whose std430
// layout differs from tight packing and accepts one that is already aligned.
// Runs on the SDL_GPU (SPIR-V) target, where std430 applies, and on Apple
// also on the Metal target, which must give the same verdicts and emit the
// packed twin struct that makes the MSL layout tight. It also checks that
// separately declared textures and samplers pair up, and that shapes lub
// can't bind fail the compile instead of reaching the GPU. The WGSL target
// (what the web player runs) checks the pairing and the bind groups the
// webgpu backend expects.
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

// `pairs` lists "<v|f|c>:<texture>=<sampler>" separated by spaces; each must
// appear in the reflection.
static void check_pairs(const char *label, const ShaderReflection *refl,
                        const char *pairs) {
  char buf[256];
  snprintf(buf, sizeof(buf), "%s", pairs ? pairs : "");
  for (char *tok = strtok(buf, " "); tok; tok = strtok(NULL, " ")) {
    SglShaderStage stage = tok[0] == 'v'   ? SGL_STAGE_VERTEX
                           : tok[0] == 'c' ? SGL_STAGE_COMPUTE
                                           : SGL_STAGE_FRAGMENT;
    char *tex = tok + 2;
    char *smp = strchr(tex, '=');
    *smp++ = '\0';
    int found = 0;
    for (int i = 0; i < refl->tex_count; i++) {
      const ShaderTexture *t = &refl->texs[i];
      if (t->stage == stage && strcmp(t->name, tex) == 0) {
        found = 1;
        CHECK(strcmp(t->smp_name, smp) == 0 && t->smp_slot >= 0,
              "%s: %s paired with '%s' (slot %d), want '%s'", label, tex,
              t->smp_name, t->smp_slot, smp);
      }
    }
    CHECK(found, "%s: texture %s not reflected", label, tex);
  }
}

static void report(const char *label, int ok, const char *err_needle,
                   const char *err) {
  if (err_needle) {
    CHECK(!ok, "%s: compile should have failed", label);
    CHECK(strstr(err, err_needle) != NULL, "%s: error lacks '%s': %s", label,
          err_needle, err);
  } else {
    CHECK(ok, "%s: compile failed: %s", label, err);
  }
  printf("%s: %s%s%s\n", ok == !err_needle ? "PASS" : "FAIL", label,
         ok ? "" : " -> ", ok ? "" : err);
}

static void expect_graphics(const char *label, const char *vs, const char *fs,
                            const char *pairs, const char *err_needle) {
  ShaderBlob vsb = {0}, fsb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok =
      shader_compile(vs, fs, g_target, &vsb, &fsb, &refl, err, sizeof(err));
  if (ok && !err_needle)
    check_pairs(label, &refl, pairs);
  if (ok) {
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
  }
  report(label, ok, err_needle, err);
}

static void expect_fs(const char *label, const char *decls, const char *body,
                      const char *pairs, const char *err_needle) {
  char fs[1024];
  snprintf(fs, sizeof(fs),
           "%s\nstruct FSIn { float2 uv : TEXCOORD0; };\n"
           "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
           "  return %s; }\n",
           decls, body);
  expect_graphics(label, kQuadVs, fs, pairs, err_needle);
}

static void run_texture_target(ShaderTargetBackend target) {
  g_target = target;
  expect_fs("macro texture", "LUB_TEXTURE2D(a);", "LUB_SAMPLE(a, i.uv)", NULL,
            NULL);
  expect_fs("named samplers",
            "Texture2D a; SamplerState a_sampler;\n"
            "Texture2D b; SamplerState b_sampler;",
            "a.Sample(a_sampler, i.uv) + b.Sample(b_sampler, i.uv)",
            "f:a=a_sampler f:b=b_sampler", NULL);
  expect_fs("named samplers in the other order",
            "Texture2D a; Texture2D b; SamplerState b_s; SamplerState a_s;",
            "a.Sample(a_s, i.uv) + b.Sample(b_s, i.uv)", "f:a=a_s f:b=b_s",
            NULL);
  expect_fs("sampler declared before its texture",
            "SamplerState albedoSampler; Texture2D albedo;",
            "albedo.Sample(albedoSampler, i.uv)", "f:albedo=albedoSampler",
            NULL);
  expect_fs("longest texture name wins",
            "Texture2D tex; Texture2D tex_detail;\n"
            "SamplerState tex_detail_s; SamplerState tex_s;",
            "tex.Sample(tex_s, i.uv) + tex_detail.Sample(tex_detail_s, i.uv)",
            "f:tex=tex_s f:tex_detail=tex_detail_s", NULL);
  expect_fs("unnamed sampler by order", "Texture2D a; SamplerState s;",
            "a.Sample(s, i.uv)", "f:a=s", NULL);
  expect_fs("Load-only texture next to a named sampler",
            "Texture2D img; SamplerState img_sampler; Texture2D lut;",
            "img.Sample(img_sampler, i.uv) + lut.Load(int3(0, 0, 0))",
            "f:img=img_sampler", NULL);
  expect_fs("unused sampler",
            "Texture2D a; SamplerState a_sampler; SamplerState spare;",
            "a.Sample(a_sampler, i.uv)", "f:a=a_sampler", NULL);
  expect_fs("unused texture next to an unnamed sampler",
            "Texture2D a; SamplerState s; Texture2D spare;",
            "a.Sample(s, i.uv)", "f:a=s", NULL);
  expect_fs("shared sampler", "Texture2D a; Texture2D b; SamplerState s;",
            "a.Sample(s, i.uv) + b.Sample(s, i.uv)", NULL, "can't be shared");
  expect_fs("Load-only texture next to an unnamed sampler",
            "Texture2D img; SamplerState s; Texture2D lut;",
            "img.Sample(s, i.uv) + lut.Load(int3(0, 0, 0))", NULL,
            "only read with Load");
  expect_fs("used sampler without texture",
            "Texture2D a; SamplerState a_sampler; SamplerState extra;",
            "a.Sample(a_sampler, i.uv) + a.Sample(extra, i.uv)", NULL,
            "'extra'");
  expect_fs("texture array", "Texture2D texs[2]; SamplerState s;",
            "texs[0].Sample(s, i.uv)", NULL, "texture arrays");

  // The vertex stage pairs on its own, also when it reuses a fragment-stage
  // global's name.
  const char *vs_tex =
      "Texture2D hm; SamplerState %s;\n"
      "struct VSOut { float2 uv : TEXCOORD0; float4 pos : SV_Position; };\n"
      "[shader(\"vertex\")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {\n"
      "  float2 p = float2((vid << 1) & 2, vid & 2);\n"
      "  float h = hm.SampleLevel(%s, p, 0.0).r;\n"
      "  VSOut o; o.uv = p; o.pos = float4(p * 2.0 - 1.0, h, 1.0);\n"
      "  return o; }\n";
  char vs[1024];
  snprintf(vs, sizeof(vs), vs_tex, "hm_sampler", "hm_sampler");
  expect_graphics("vertex-stage sampler", vs,
                  "[shader(\"fragment\")] float4 fs_main() : SV_Target {\n"
                  "  return float4(1, 1, 1, 1); }\n",
                  "v:hm=hm_sampler", NULL);
  snprintf(vs, sizeof(vs), vs_tex, "s", "s");
  expect_graphics("same sampler name in both stages", vs,
                  "Texture2D albedo; SamplerState s;\n"
                  "struct FSIn { float2 uv : TEXCOORD0; };\n"
                  "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target "
                  "{ return albedo.Sample(s, i.uv); }\n",
                  "v:hm=s f:albedo=s", NULL);

  ShaderBlob csb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok = shader_compile_compute(
      "Texture2D src; SamplerState src_sampler;\n"
      "RWStructuredBuffer<float4> dst;\n"
      "[shader(\"compute\")][numthreads(1, 1, 1)]\n"
      "void cs_main(uint3 tid : SV_DispatchThreadID) {\n"
      "  dst[tid.x] = src.SampleLevel(src_sampler, float2(0.5, 0.5), 0.0); }\n",
      g_target, &csb, &refl, err, sizeof(err));
  if (ok) {
    check_pairs("compute-stage sampler", &refl, "c:src=src_sampler");
    shader_blob_free(&csb);
  }
  report("compute-stage sampler", ok, NULL, err);

  // A global outside a cbuffer lands in Slang's own uniform block, which
  // lub's SPIR-V layout has no place for.
  if (target == SHADER_TARGET_SDLGPU)
    expect_fs("loose global", "float4 tint;", "tint", NULL,
              "descriptor layout");
}

// Every WGSL resource declaration names the group the webgpu backend binds
// it from: uniform blocks in group 0, everything else in group 1. Storage
// textures are write-only, the only access WebGPU core allows.
static void check_wgsl(const char *label, const ShaderBlob *b) {
  const char *s = (const char *)b->spirv;
  CHECK(s && strstr(s, "fn ") != NULL, "%s: blob is not WGSL", label);
  if (!s)
    return;
  CHECK(strstr(s, "read_write>") == NULL, "%s: read_write storage texture",
        label);
  for (const char *line = s; line && *line;) {
    const char *end = strchr(line, '\n');
    size_t n = end ? (size_t)(end - line) : strlen(line);
    char buf[512];
    snprintf(buf, sizeof(buf), "%.*s", (int)n, line);
    if (strstr(buf, "@binding(")) {
      const char *want =
          strstr(buf, "var<uniform>") ? "@group(0)" : "@group(1)";
      CHECK(strstr(buf, want) != NULL, "%s: %s not in %s", label, buf, want);
    }
    line = end ? end + 1 : NULL;
  }
}

static void run_wgsl_groups(void) {
  const char *fs =
      "cbuffer Params { float4 tint; };\n"
      "LUB_TEXTURE2D(albedo);\n"
      "struct FSIn { float2 uv : TEXCOORD0; };\n"
      "[shader(\"fragment\")] float4 fs_main(FSIn i) : SV_Target {\n"
      "  return LUB_SAMPLE(albedo, i.uv) * tint; }\n";
  ShaderBlob vsb = {0}, fsb = {0}, csb = {0};
  ShaderReflection refl;
  char err[1024] = {0};
  int ok = shader_compile(kQuadVs, fs, SHADER_TARGET_WGSL, &vsb, &fsb, &refl,
                          err, sizeof(err));
  if (ok) {
    check_wgsl("wgsl groups vs", &vsb);
    check_wgsl("wgsl groups fs", &fsb);
    shader_blob_free(&vsb);
    shader_blob_free(&fsb);
  }
  report("wgsl groups", ok, NULL, err);

  ok =
      shader_compile_compute("RWTexture2D<float4> dst;\n"
                             "[shader(\"compute\")][numthreads(8, 8, 1)]\n"
                             "void cs_main(uint3 tid : SV_DispatchThreadID) {\n"
                             "  dst[tid.xy] = float4(1, 0, 0, 1); }\n",
                             SHADER_TARGET_WGSL, &csb, &refl, err, sizeof(err));
  if (ok) {
    check_wgsl("wgsl storage texture", &csb);
    CHECK(refl.storage_tex_count == 1, "wgsl storage texture: count %d",
          refl.storage_tex_count);
    shader_blob_free(&csb);
  }
  report("wgsl storage texture", ok, NULL, err);
}

int main(void) {
  run_target(SHADER_TARGET_SDLGPU);
  run_texture_target(SHADER_TARGET_SDLGPU);
  run_texture_target(SHADER_TARGET_WGSL);
  run_wgsl_groups();
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
