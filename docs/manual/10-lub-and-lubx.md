# `Lub API` と `Lubx API`

ゲームから使う API は、最小限の土台である `Lub API` (`using Lub;`) と、その上に純粋な C# で書かれた便利層の `Lubx API` (`using Lubx;`) の 2 層に分かれている。

## 2 層の役割と使い分け

- `Lub API` (`cs-lib/lub_stub.cs`): `liblub` が直接提供する機能。起動設定 (`App`)、GPU 描画 (`Gfx`)、入力 (`Input`)、ファイル入出力 (`Io`)、物理 (`Phys2d` / `Phys3d`)、音 (`Audio`)、XR (`Xr`)、ホスト連携 (`Host`)、計測 (`Profiler` / `Sys`) など。
- `Lubx API` (`cs-lib/lubx/`): `Lub API` を組み合わせて作る便利部品。`SpriteBatch` / `Atlas` / `Shapes` / `Text` (2D)、`Renderer3d` / `Mesh3d` / `Shapes3d` / `Bones` (3D)、`Camera2d` / `Camera3d`、`FixedStep`、`Assets`、`Sfx`、`Rand`、`XrAnchor` など。

使い分けの目安:

- まず `Lubx API` から書き始める。
- 描画やリソースを細かく制御したくなったら `Gfx` などの `Lub API` へ降りる。
- `Lubx API` に無い機能や挙動を変えたい部品があるときは、該当する C# ソースを読んで自分のプロジェクトへ写して改造する。ゲーム固有の表現を自分で組み立てるのが想定された使い方。

## `key` の付け方

リソースの `key` はアプリ全体で 1 つの名前空間を共有する文字列で、フレームやホットリロードを跨いで同じ対象を指す。

- 複数のゲームやモジュールを共存させるときは、`game_a/floor` のように親の名前を `/` で繋いで衝突を避ける (エンジン側にスコープ機能はなく、単なる文字列の連結)。
- `Lubx` でリソースを宣言する部品 (`SpriteBatch` / `Atlas` / `Text` / `MeshText` / `Mesh3d` / `Renderer3d` / `Assets` / `Sfx.Synth`) は、先頭引数に `key` を取る。
- 内部に複数リソースを持つ部品は、その `key` の配下 (`new Renderer3d("game_a/ren")` なら `game_a/ren/hdr` 等) に宣言する。

```csharp
var batch = new SpriteBatch("game_a/ui", 320, 180);
var ren = new Renderer3d("game_a/ren");
var floor = Assets.Shader("game_a/floor", vsPath, fsPath);
```

- 同じ `key` を渡した部品は同じリソースを共有するため、中身の違うものに同じ `key` を渡さない。
- `Sfx.Blip` / `Sfx.Noise`、`SpriteBatch.Rect` / `Disc`、`SpriteBatch` のシェーダのように内容が固定のものは共通の `key` を使うため、呼び出し側で `key` を指定しない。

## 2D 描画 (`SpriteBatch` / `Atlas` / `Text`)

2D の図形・画像・文字は `SpriteBatch` で論理解像度のピクセル座標 (左上原点、+Y 下) にまとめて描く:

```csharp
static readonly SpriteBatch batch = new SpriteBatch("game/ui", 320, 180);
static readonly Atlas sheet = Atlas.FromPng("game/sheet", "data/sheet.png");
static Text? font;
static float angle;

public static void OnFrame(float dt)
{
    angle += dt;
    if (!sheet.Ensure()) return;
    // Text は内部で読むため、フォントファイルが読める状態になるまで待つ
    Io.LoadBytes("data/font.ttf", out var ttf, out _, out _, out _);
    if (ttf == null) return;
    font ??= new Text("game/font", "data/font.ttf", 16);
    font.Ensure();

    Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0.1f, 0.1f, 0.15f, 1 } });
    batch.Begin();
    batch.Rect(8, 8, 120, 32, Color.Rgb(0.2f, 0.2f, 0.3f, 0.9f));
    batch.Disc(280, 90, 12, Color.Rgb(1, 0.8f, 0.2f));
    batch.Quad(sheet, new Rect(0, 0, 16, 16), 40, 80, 16, 16);
    batch.Sprite(sheet, new Rect(16, 0, 16, 16), 160, 90, 16, 16, angle);
    font.Draw(batch, "SCORE 100", 16, 28);
    batch.Flush();
    Gfx.EndPass();
}
```

- `Begin` → `Rect` / `Disc` / `Quad` (左上指定) / `Sprite` (中心と回転角指定) / `Text.Draw` → `Flush` の順でパス内に書く。
- `Atlas.Ensure()` と `Text.Ensure()` は毎フレーム呼んでテクスチャ宣言を維持する。

### 重なり順

`SpriteBatch` は `Begin` から `Flush` までに積んだスプライトをアトラス単位で 1 回の draw にまとめる。
そのため描画の重なり順は、呼んだ順ではなく `Begin` の後に各アトラスが初めて登場した順になる (同じアトラス内は呼んだ順)。
`Text` の文字もフォントのアトラスの 1 つとして同じ扱いになる。

異なるアトラスの絵を呼んだ順に重ねたいときは、層ごとに `Begin` と `Flush` を分ける:

```csharp
batch.Begin();
batch.Quad(bg, src, 0, 0, 320, 180);      // 背景のアトラス
batch.Flush();
batch.Begin();
batch.Quad(chara, src, 100, 80, 16, 16); // 背景の上に重ねるアトラス
batch.Flush();
```

## `Renderer3d` のカメラとシェーダ差し替え

`Renderer3d.Begin` に渡す `Camera` の `Fov`・`Near`・`Far` は、省略すると 60 度・0.1・100 になる。太陽や星など遠い背景を置くときは `Far` を広げる。

### シェーダの差し替え (`Draw3dOpts.Shader`)

`Draw3dOpts.Shader` に自前のシェーダを渡すと、その draw だけ `Renderer3d` の既定シェーダの代わりに使われる:

- 差し替えたシェーダは forward パスでのみ使われる。出力先は linear HDR で、後段で `Renderer3d` が AgX トーンマップを掛ける。
- 頂点色と `Tint` は sRGB 値のため、既定シェーダは `pow(color, 2.2)` で linear に直してからライティングしている。
- 影パスは差し替えに関わらず組み込みシェーダでメッシュを描くため、差し替えシェーダ内で頂点を動かしても影は動かない。
- `Blend` を指定した draw は不透明オブジェクトの後に深度書き込みなしで描かれ、影を落とさない。

### 頂点と uniform の規約

`Renderer3d` は `Mesh3d` の頂点を `verts`、インデックスを `indices` として渡す:

```slang
struct V {
  float3 pos;
  float pad0;
  float3 normal;
  float pad1;
  float3 color;
  float pad2;
  float2 mr; // metallic, roughness
  float2 pad3;
};
StructuredBuffer<V> verts;
```

スキニング付きメッシュ (`Mesh3d.Skinned`) は `V` の末尾に `float4 skin;` (`j0, w0, j1, w1`) が付き、uniform に `bones` が加わる。

| uniform 名 | 型 | 内容 |
| --- | --- | --- |
| `mvp` | `float4x4` | model から clip への変換 |
| `model` | `float4x4` | model から world への変換 |
| `light_mvp` | `float4x4` | world から shadow map の clip への変換 |
| `tint` | `float4` | `Draw3dOpts.Tint` (sRGB、既定は白) |
| `light_dir` | `float4` | 光源へ向かう向き (world、正規化済み) |
| `light_col` | `float4` | 光の色 × 強さ |
| `sky_col` | `float4` | 半球環境光の上の色 (`w` は強さ) |
| `ground_col` | `float4` | 半球環境光の下の色 |
| `cam_pos` | `float4` | カメラ位置 (world) |
| `shadow_p` | `float4` | `x` = 1 / shadow map 一辺、`y` = bias、`z` = 影有効なら 1 |
| `bones` | `float4x4[16]` | スキニング付きメッシュのみ |

頂点・フラグメント双方のソース先頭に `Renderer3d.ShaderUniforms` (スキニング時は `Renderer3d.SkinnedShaderUniforms`) を連結して宣言し、シェーダからは `r3d.mvp` のように参照する:

```csharp
var sh = Gfx.UseShader("surface",
    Renderer3d.ShaderUniforms + vs,
    Renderer3d.ShaderUniforms + fs, version);
```

- uniform の並びは完全一致が必須のため、独自 uniform を足したい場合は `Renderer3d` を写して拡張する。
- テクスチャには影用の `shadow_map` (`LUB_TEXTURE2D(shadow_map)`) が渡されるほか、`Draw3dOpts.Textures` で追加のテクスチャを渡せる。
- `lub export` で書き出すときは、`OnExport` 内で `Renderer3d` の `Begin` / `End` を 1 回呼び、差し替えシェーダも `Gfx.UseShader` (または `Assets.Shader`) で宣言しておく。
