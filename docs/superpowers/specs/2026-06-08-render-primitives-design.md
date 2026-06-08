# Render Primitive Design for Upscaling-Friendly Pipelines

`lub` core に FSR/TAAU そのものを入れず、`lubx` が標準 3D pipeline や
upscaler を組めるだけの低レベル描画 primitive を整える。目的は API surface を
増やさず、sokol / SDL_GPU 互換を維持したまま、通常の post-process / lighting /
deferred rendering にも効く改善にすること。

この文書は実装前提の設計メモであり、FSR SDK 統合の設計ではない。

## 1. Goals / Non-Goals

### Goals

- Lua/Haxe 側の API shape をほぼ変えない。
- `Gfx.draw` / `Gfx.dispatch` / `Gfx.useTexture` の既存モデルを保つ。
- sokol backend と SDL_GPU backend の差分を core 内に閉じ込める。
- fragment uniform、depth sampling、小さい render-target format、compute texture
  output を段階的に使えるようにする。
- `lubx` が TAAU-lite / RCAS / future FSR-like pipeline を所有できる土台を作る。
- FSR 以外の rendering quality / bandwidth / post-process にも効果がある変更にする。

### Non-Goals

- `lub` core に `enableFsr2` / `upscale` / `defaultPipeline` のような機能 API を追加する。
- core が velocity / depth / reactive mask / exposure などの rendering semantic を所有する。
- render graph、explicit barrier、resource state API を Lua/Haxe に露出する。
- FSR2/FSR3 SDK をそのまま runtime core へ接続する。
- すべてを一度に実装する。初期実装は fragment uniform と format/depth 周りを優先する。

## 2. Core / lubx Boundary

`lub` core は以下だけを提供する。

- texture / buffer / shader / pipeline / pass / dispatch の backend-independent primitive
- resource lifetime と hot reload の一貫性
- backend 差分の吸収
- capture / profile / validation に必要な診断

`lubx` が所有するもの。

- fullscreen pass helper
- render-target pool / ping-pong history
- deferred / forward pipeline convention
- velocity buffer convention
- jitter / history resolve / neighborhood clamp
- RCAS / CAS / TAAU-lite / FSR1 fallback
- future FSR2-like or SDK wrapper

つまり core に入れるのは「upscaler」ではなく、upscaler を含む多くの renderer が必要とする
汎用 GPU primitive だけ。

## 3. Current Constraints

### Uniform block stage is fixed to vertex for graphics

現状の graphics uniform は backend 側で vertex stage 固定になっている。既存 sample では
fragment shader に定数を渡したい場合、vertex shader の uniform から varying へ転送する
回避を使っている。

この制約は以下に悪影響がある。

- fullscreen post-process shader が読みにくい。
- lighting / grading / sharpen / TAAU で fragment-side constants を自然に書けない。
- FSR1 EASU/RCAS の定数を shader 側で素直に扱えない。
- 将来の material shader で VS/FS 両方に uniform block を置きにくい。

### Texture formats are coarse

現状の公開 format は `RGBA8` / `R8` / `RG8` / `RGBA16F` / `RGBA32F` と depth 系。
velocity、linear depth、mask、luma、exposure-like intermediate に `RGBA16F` を使うと
帯域とメモリが過剰になる。

### Depth target sampling contract is not explicit

depth target を作る経路はあるが、後段 pass で sampled texture として使う前提が core の
contract として固まっていない。SSAO / fog / DOF / TAAU disocclusion では depth sampling が
自然に必要になる。

### Compute can write buffers but not textures

`Gfx.dispatch` は storage buffer を扱えるが、`RWTexture2D` / storage image 相当は扱えない。
これは初期 TAAU-lite には必須ではないが、GPU blur、mip/pyramid、histogram、future SDK
upscaler では必要になる。

## 4. Design Principles

1. **Public API は既存形を保つ**
   - `draw(count, resources, opts)`
   - `dispatch(x, y, z, resources, opts)`
   - `useTexture(key, w, h, fmt, px, version, opts)`

2. **shader reflection を source of truth にする**
   ユーザーが binding kind を明示しない。shader が `Texture2D` / `Sampler2D` /
   `RWTexture2D` / `ConstantBuffer` を宣言し、runtime が reflection で backend binding へ
   解決する。

3. **barrier / resource state は公開しない**
   `begin_pass` / `end_pass` / `dispatch` の境界を synchronization boundary として扱い、
   backend が必要な遷移を行う。明示 barrier が必要になったら設計を見直すが、初期 scope
   では公開しない。

4. **feature は汎用名にする**
   `fsr` / `taau` ではなく、`storage` / `format` / `uniform stage` のような GPU primitive
   として実装する。

5. **unsupported backend は明確に fail する**
   silent fallback は避ける。shader compile / resource creation / dispatch 時に、backend が
   対応できない組み合わせを error として返す。

## 5. Phase A: No New Public API Shape

Phase A は Lua/Haxe の呼び出し形を変えずに進める。主に reflection と backend mapping の修正。

### 5.1 Stage-aware uniform blocks

#### Public API

変更なし。

```haxe
Gfx.draw(6, {
  verts: quad,
  scene: colorTex,
  uniforms: {
    params: lua.Table.fromArray([exposure, sharpness, 0.0, 0.0])
  }
}, { shader: shader, depth: false, cull: Gfx.NONE });
```

shader 側は fragment stage に直接 `ConstantBuffer` を置ける。

```hlsl
struct Params { float4 params; };
ConstantBuffer<Params> u;

LUB_TEXTURE2D(scene);

[shader("fragment")]
float4 fs_main(FSIn i) : SV_Target {
    float sharpness = u.params.y;
    return float4(LUB_SAMPLE_LOD(scene, i.uv).rgb * sharpness, 1.0);
}
```

#### Internal changes

`ShaderUniformBlock` に stage 情報を追加する。

```c
typedef enum SglShaderStage {
  SGL_STAGE_VERTEX = 1,
  SGL_STAGE_FRAGMENT = 2,
  SGL_STAGE_COMPUTE = 3,
} SglShaderStage;

typedef struct ShaderUniformBlock {
  char name[32];
  int slot;
  SglShaderStage stage;
  int size_floats;
  int member_count;
  ShaderUniformMember members[SGL_MAX_UB_MEMBERS];
} ShaderUniformBlock;
```

`RenderBackend.apply_uniforms` は internal vtable なので signature を変えてよい。

```c
void (*apply_uniforms)(SglShaderStage stage, int ub_slot,
                       const void *data, size_t bytes);
```

`lua_api.c` は `resources.uniforms` から **すべての reflected uniform block** を pack して apply する。
これは公開 API を増やさず、VS/FS に別 block がある shader も扱えるようにする。

- member 名で pack する既存方式を維持する。
- block 名を Lua 側に露出しない。
- 同名 member が複数 block にある場合は同じ値が両方に入る。これは単純で予測可能。
- 未指定 member は 0 のまま。

#### Native reflection strategy

必要なのは「どの uniform block がどの stage で使われるか」だけ。

実装候補は 2 つある。

1. VS / FS を stage ごとに compile + reflect して merge する。
   - wasm path はすでに stage ごとに compile して reflection JSON を merge している。
   - native path も同じ構造に寄せると stage 情報を保持しやすい。
   - descriptor set patching は現在も VS blob / FS blob に別々にかけているので相性がよい。

2. 現在の combined module は維持し、entry point reflection または per-entry SPIR-V scan から
   stage usage を付ける。
   - 既存構造への変更は小さい可能性がある。
   - Slang API 依存が強くなりやすい。

初期実装では 1 を優先する。stage-specific reflection の方が wasm path と揃い、SDL_GPU の
per-stage uniform count とも自然に合う。

#### Backend mapping

sokol:

- `sg_shader_desc.uniform_blocks[slot].stage` に `SG_SHADERSTAGE_VERTEX` または
  `SG_SHADERSTAGE_FRAGMENT` を設定する。
- `sg_apply_uniforms(slot, ...)` は stage を引数に取らないため、shader desc 側の stage 設定で
  解決される。

SDL_GPU:

- vertex shader create info の `num_uniform_buffers` は VS block 数。
- fragment shader create info の `num_uniform_buffers` は FS block 数。
- apply 時は stage に応じて `SDL_PushGPUVertexUniformData` /
  `SDL_PushGPUFragmentUniformData` を呼ぶ。
- compute は既存どおり `SDL_PushGPUComputeUniformData`。

#### Benefits

- postprocess shader が素直に書ける。
- FSR1/RCAS/TAAU-lite の constants を FS 側で扱える。
- `12_sfb` / `14_sponza` の VS-to-FS uniform forwarding を削れる。
- future material shader で VS/FS の責務分離が自然になる。

### 5.2 Pack all graphics uniform blocks

`lua_api.c` の `draw` は現在 first uniform block だけを pack している。これをすべての block に
拡張する。

公開 API は変えない。

```haxe
uniforms: {
  model: ...,
  view_proj: ...,
  material: ...,
  params: ...
}
```

各 reflected block は自分の member だけ拾う。これにより、shader author は VS 用 block と
FS 用 block を自然に分けられる。

制約:

- block 数上限は既存 `SGL_MAX_UNIFORM_BLOCKS` に従う。
- Haxe/Lua 側から block 単位 binding は指定しない。
- member 名衝突の解決はしない。同名なら同じ値が入る。

### 5.3 Small render-target formats

追加候補:

```c
SGL_PF_R16F
SGL_PF_RG16F
SGL_PF_R32F
```

Haxe extern:

```haxe
@:native("R16F") public static var R16F(default, null):Int;
@:native("RG16F") public static var RG16F(default, null):Int;
@:native("R32F") public static var R32F(default, null):Int;
```

用途:

| Format | 用途 |
|---|---|
| `R16F` | linear depth copy, luma, mask, rough scalar intermediate |
| `RG16F` | velocity / motion vector, packed scalar pairs |
| `R32F` | high precision linear depth / debug / future reduction input |

初期実装では color render target + sampled texture として使えることを条件にする。
storage texture 対応は Phase B で扱う。

Backend mapping:

- sokol: `SG_PIXELFORMAT_R16F`, `SG_PIXELFORMAT_RG16F`, `SG_PIXELFORMAT_R32F`
- SDL_GPU: `SDL_GPU_TEXTUREFORMAT_R16_FLOAT`, `SDL_GPU_TEXTUREFORMAT_R16G16_FLOAT`,
  `SDL_GPU_TEXTUREFORMAT_R32_FLOAT`

Validation:

- backend が format を render target または sample として使えない場合、resource creation で error。
- `RGBA16F` を使っていた velocity / depth-like intermediate を `RG16F` / `R16F` に落とす sample を追加。

### 5.4 Depth texture sampling contract

まず `DEPTH32F` を対象に contract を固める。

Public API は変えない。

```haxe
var depth = Gfx.useTexture("scene_depth", w, h, Gfx.DEPTH32F, null, 1, {
  target: true,
  filter: Gfx.NEAREST,
  wrap: Gfx.CLAMP
});

Gfx.beginPass({ target: color, depth_target: depth, clear_depth: 1.0 });
// draw scene
Gfx.endPass();

Gfx.draw(6, { verts: quad, depth_tex: depth }, { shader: ssaoShader, depth: false });
```

Contract:

- depth target は pass 終了後、sampled texture として bind できる。
- filtering は初期実装では `NEAREST` を推奨。linear depth filtering は backend 差があるため
  sample 側で明示的に扱う。
- `DEPTH24_STENCIL8` sampling は初期 scope 外。debug/capture 用ではなく depth/stencil attachment
  として扱う。
- `read_texture` は引き続き depth texture 非対応でよい。

Benefits:

- `gPosition` を常に持たなくても SSAO / fog / DOF / TAAU disocclusion を組める。
- bandwidth が下がる。
- depth pyramid や linear-depth copy へ進みやすい。

## 6. Phase B: Compute Texture Output Through Existing dispatch

Phase B は optional texture flag と reflection 拡張を伴う。公開 function は増やさない。

### 6.1 Texture creation flag

`useTexture` の `opts` に optional flag を追加する。

```haxe
var out = Gfx.useTexture("blur_out", w, h, Gfx.RGBA16F, null, 1, {
  target: true,
  filter: Gfx.LINEAR,
  wrap: Gfx.CLAMP,
  storage: true
});
```

`storage: true` の意味:

- sampled texture としても使える。
- compute shader の write-only storage texture としても使える。
- same compute shader 内で read/write する simultaneous access は初期 scope 外。

将来必要になった場合だけ `storage_rw: true` を検討する。最初から出さない。

### 6.2 Shader reflection

`ShaderReflection` に storage texture entry を追加する。

```c
#define SGL_MAX_STORAGE_TEXTURES 4

typedef struct ShaderStorageTexture {
  char name[32];
  int slot;
  bool readonly; // initial scope: false only for RW/write storage images
} ShaderStorageTexture;
```

Slang source example:

```hlsl
LUB_TEXTURE2D(src);
RWTexture2D<float4> dst;

[numthreads(8, 8, 1)]
[shader("compute")]
void cs_main(uint3 tid : SV_DispatchThreadID) {
    float2 uv = (float2(tid.xy) + 0.5) * inv_size;
    dst[tid.xy] = LUB_SAMPLE_LOD(src, uv);
}
```

`dispatch` の `resources` table は既存形を維持する。

```haxe
Gfx.dispatch(groupsX, groupsY, 1, {
  src: inputTex,
  dst: outputTex,
  uniforms: { params: ... }
}, { shader: computeShader });
```

runtime は reflection を見て `src` を sampled texture、`dst` を storage texture として bind する。
ユーザーは binding kind を指定しない。

### 6.3 Backend mapping

sokol:

- image desc: `.usage.storage_image = true`
- storage image view を作成し、texture view と同じ resource entry で保持する。
- compute shader desc の `views[slot].storage_image.stage = SG_SHADERSTAGE_COMPUTE`
- dispatch binding で `sg_bindings.views[slot] = storage_image_view`

SDL_GPU:

- texture usage に `SDL_GPU_TEXTUREUSAGE_COMPUTE_STORAGE_WRITE` を追加する。
- compute pipeline create info の `num_readwrite_storage_textures` を reflection から設定する。
- dispatch 時に `SDL_BindGPUComputeStorageTextures` を呼ぶ。
- sampled input texture は `SDL_BindGPUComputeSamplers` を使う。

### 6.4 Synchronization model

公開 barrier は追加しない。

Initial contract:

- render pass と dispatch はネスト不可。
- `end_pass` 後の texture は次の `draw` / `dispatch` で read 可能。
- `dispatch` 後の storage texture は次の `draw` / `dispatch` で read 可能。
- 同一 dispatch 内で同じ texture を read と write の両方に使うことは未定義。runtime は可能なら検出して error。

この contract で、blur、copy、TAAU resolve、RCAS、mip-like downsample chain は組める。

## 7. lubx Pipeline Direction

core primitive が揃った後、`lubx` は以下を持てる。

### 7.1 FullscreenPass

`lubx.FullscreenPass` は shader / quad buffer / target begin/end をまとめる軽い helper。
core API ではなく library code。

```haxe
pass.run(target, {
  scene: color,
  depth_tex: depth,
  uniforms: { params: ... }
});
```

### 7.2 Rcas / Cas

FSR1/FSR2 proper とは独立した sharpen pass。

- input color
- output target or mainTex
- sharpness

fragment uniform stage 対応だけで実装できる。storage texture は不要。

### 7.3 TAAU-lite

`14_sponza` ベースの proving ground。

Inputs:

- low-res color
- depth or linear depth
- velocity `RG16F`
- previous high-res history
- jitter sequence

Outputs:

- high-res resolved color
- new history

Pass outline:

1. scene render at internal resolution
2. velocity/depth available
3. fullscreen resolve to display resolution
4. reproject history
5. neighborhood clamp
6. disocclusion rejection
7. RCAS sharpen

初期版は graphics fullscreen pass でよい。compute storage texture は後から resolve pass の
最適化として使う。

### 7.4 Future FSR paths

- FSR1 helper は optional fallback として `lubx` に置ける。
- FSR2/3 SDK proper は、storage texture / compute sampled texture / more formats が揃った後に
  wrapper として検討する。
- SDK proper を入れる場合でも core API に `FSR` 名は出さない。

## 8. Verification Plan

### Unit / smoke samples

1. `fragment_uniform_texture`
   - FS に `ConstantBuffer` と texture を同時に置く。
   - uniform 値で色を変える。
   - sokol / SDL_GPU golden を取る。

2. `multi_uniform_blocks`
   - VS block と FS block を同時に宣言する。
   - `resources.uniforms` の 1 table から両方が pack されることを確認する。

3. `format_targets`
   - `R16F` / `RG16F` / `R32F` を render target として作成し、sample して visible color に変換する。

4. `depth_sample`
   - `DEPTH32F` target に scene depth を書く。
   - 次 pass で sample して grayscale 表示する。

5. `compute_storage_texture` (Phase B)
   - compute shader が storage texture に gradient を書く。
   - 次 draw pass で sample して表示する。

### Existing sample cleanup

- `12_sfb` / `14_sponza` の VS-to-FS uniform forwarding を減らす。
- `gPosition` しか使っていない depth-like postprocess の一部を depth sampling に寄せる。
- velocity buffer は `RGBA16F` ではなく `RG16F` に寄せる。

### Benchmark / profiling

この設計自体は sprite benchmark 対象ではない。rendering path の確認は `14_sponza` を使う。
性能比較では以下を見る。

- render-target memory footprint
- postprocess pass time
- `RGBA16F` から `RG16F` / `R16F` に落とした時の GPU time
- TAAU-lite resolve pass cost

## 9. Implementation Order

1. `ShaderUniformBlock.stage` 追加。
2. native shader reflection を stage-aware merge に寄せる。
3. wasm reflection merge に stage を保存する。
4. backend shader creation で VS/FS uniform block を正しい stage に登録する。
5. `apply_uniforms(stage, slot, ...)` に変更する。
6. `lua_api.c` の `draw` を all-UB packing に変更する。
7. FS uniform + texture の smoke sample/golden を追加する。
8. `R16F` / `RG16F` / `R32F` を enum / Haxe extern / backend mapping に追加する。
9. `DEPTH32F` sampling の backend behavior を固定し、depth sample を追加する。
10. `lubx.Rcas` を追加する。
11. `14_sponza` に optional TAAU-lite spike を入れる。
12. 必要になった段階で storage texture compute を Phase B として実装する。

## 10. Risks

### Slang reflection differences

native path と wasm path で reflection の形が違う。stage-aware merge は両 path で同じ
`ShaderReflection` に正規化する必要がある。

Mitigation:

- native path を wasm path と同じ stage-separated compile/reflect model に寄せる。
- reflection dump を debug log できるようにする。

### SDL_GPU descriptor counts

SDL_GPU は shader create 時に stage ごとの uniform / sampler / storage count が必要。
reflection と SPIR-V binding rewrite がずれると pipeline creation が失敗する。

Mitigation:

- smoke sample を sokol / SDL_GPU 両方で golden 化する。
- FS uniform + texture の組み合わせを最初の検証対象にする。

### Depth sampling portability

depth format の filtering / comparison sampler / D24S8 sampling は backend 差が出やすい。

Mitigation:

- 初期 contract は `DEPTH32F` + nearest sample に限定する。
- shadow compare sampler は scope 外。

### Storage texture support

sokol と SDL_GPU の両方に概念はあるが、usage flag、view、binding count が違う。

Mitigation:

- Phase B に分離する。
- initial scope は compute write-only storage texture のみ。
- same-dispatch read/write は scope 外。

## 11. Decision Summary

- `lub` core には upscaler 名の API を入れない。
- 最初に直すべき core primitive は stage-aware uniform。
- 次に small float formats と `DEPTH32F` sampling contract を固める。
- storage texture は有用だが Phase B。TAAU-lite の初期版には必須ではない。
- `lubx` は `Rcas` と `TAAU-lite` を持ち、将来 FSR1 fallback や FSR2-like wrapper を追加できる。
