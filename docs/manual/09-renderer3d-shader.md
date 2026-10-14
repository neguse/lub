# Renderer3d の shader を差し替える

`Draw3dOpts.Shader` に自分の shader を渡すと、その draw だけ Renderer3d の既定の shader の代わりに使われる。
差し替える shader は、既定の shader と同じ頂点を読み、同じ名前の uniform を受ける。
光と影を既定と同じように計算する例は `samples/25_bowling` の `SurfaceShader` にある。

## どこで使われるか

- 差し替えた shader は forward の pass でだけ使われる。描く先は linear の HDR で、後で Renderer3d が tonemap (AgX) する。
- 頂点色と `Tint` は sRGB の値なので、既定の shader は `pow(color, 2.2)` で linear に直してから光を掛けている。
- 影の pass は、差し替えに関わらず組み込みの shader で mesh の形を描く。差し替えた shader の中で頂点を動かしても、影は動かない。
- `Blend` を指定した draw は、不透明な draw の後に depth を書かずに描かれ、影を落とさない。

## 頂点

Renderer3d は Mesh3d の頂点を `verts`、index を `indices` として渡す。
shader は `verts` を `LUB_VERTEX_ID` で引く (`manual/04-gfx.md` の vertex pulling)。

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

skinned の mesh (`Mesh3d.Skinned`) は、`V` の末尾に `float4 skin;` (j0, w0, j1, w1) が付き、uniform の `bones` で関節の行列を受ける。

## uniform

Renderer3d が渡す uniform は次のとおり。

| 名前 | 型 | 中身 |
| --- | --- | --- |
| `mvp` | `float4x4` | model から clip へ |
| `model` | `float4x4` | model から world へ |
| `light_mvp` | `float4x4` | world から shadow map の clip へ |
| `tint` | `float4` | `Draw3dOpts.Tint` (sRGB、既定は白) |
| `light_dir` | `float4` | 光へ向かう向き (world、正規化済み) |
| `light_col` | `float4` | 光の色 × 強さ |
| `sky_col` | `float4` | 半球の環境光の上の色。`w` は環境光の強さ |
| `ground_col` | `float4` | 半球の環境光の下の色 |
| `cam_pos` | `float4` | カメラの位置 (world) |
| `shadow_p` | `float4` | `x` = 1 / shadow map の一辺、`y` = bias、`z` = 影が有効なら 1 |
| `bones` | `float4x4[16]` | skinned の mesh のときだけ |

- uniform は名前で渡る。shader の uniform block (`ConstantBuffer`) の field に、同じ名前の値が入る。shader に無い名前は無視され、値の無い field は 0 になる。要る名前だけ宣言すればよく、vertex と fragment で別の block にしてよい。
- `Draw3dOpts.Uniforms` に足した名前は、上の表の値の後に入る (同じ名前なら上書きする)。値は `List<float>` で、`float4` なら 4 個、`float4x4` なら 16 個。
- テクスチャは、影の `shadow_map` を渡す (`LUB_TEXTURE2D(shadow_map)`)。`Draw3dOpts.Textures` に名前と `TextureRef` を足すと、shader の同じ名前のテクスチャに渡る。

## export するとき

`OnExport` の中で、Renderer3d の `Begin` と `End` を一度呼ぶ。`End` が既定の shader と後処理の shader を全部宣言するので、それらが export に入る。
差し替える shader も、`OnExport` の中で `Gfx.UseShader` (か `Assets.Shader`) を一度呼んで宣言する。
宣言しなかった shader は web の page で compile できず、key を名指しする error になる。
