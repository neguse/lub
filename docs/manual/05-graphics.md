# 描画とシェーダ

`Gfx` は毎フレーム宣言する immediate な GPU API。

## `Use*` — `key` と `version` によるリソース宣言

```csharp
Io.LoadText("data/cube.vs.slang", out var vs, out var vsVersion, out _, out _);
Io.LoadText("data/cube.fs.slang", out var fs, out var fsVersion, out _, out _);
if (vs == null || fs == null) return;
var shader = Gfx.UseShader("cube", vs, fs, vsVersion * 31 + fsVersion);
```

- 毎フレーム同じ `key` で呼ぶ。`version` が一致すればキャッシュを返し、変わっていれば作り直す。
- `version` を渡して一致した宣言はデータ (`UseBuffer` の配列など) を読まないため、data に `null` を渡せる。一致するリソースが無ければ何も変えず `null` を返すため、`UseBuffer(key, type, null, v) ?? UseBuffer(key, type, Build(), v)` と書ける (`UseTexture` は `px = null` が空テクスチャ宣言になる点だけ例外)。
- `version` を省略すると「内容が変わった」宣言になり、毎回アップロードして新しい実効 `version` を発行する (data に `null` は不可)。毎フレーム再アップロードを避けたいときは前回の戻り値参照の `Version` を渡す。
- 複数ファイルを束ねるときは `a * 31 + b` のように順序依存で結合する (XOR は同一内容や入れ替えで打ち消し合う)。
- 同じ `key` で方式 (定数 / 省略 / ハッシュ) を混ぜない。また 1 フレーム内で同じ `key` の `UseShader` / `UseBuffer` を別 `version` で宣言するとエラーになる。
- 1 フレーム内で同じ `key` の `UseTexture` (または `version` 省略の `UseBuffer`) を書き換えた場合、書き換え前に記録した draw は古い内容、書き換え後の draw は新しい内容を読む。
- 戻り値の参照は宣言したフレーム内だけで使う。

## Pass と Draw

```csharp
Gfx.BeginPass(new PassOpts
{
    Target = Gfx.MainTex,
    ClearColor = new float[] { 0.05f, 0.05f, 0.15f, 1.0f },
});
var bindings = new Bindings();
bindings.Set("verts", buf);
bindings.Set("mvp", mvp);
Gfx.Draw(36, bindings, new DrawOpts { Shader = shader });
Gfx.EndPass();
```

- 描画は `BeginPass` / `EndPass` で囲む。画面へは `Target = Gfx.MainTex`、オフスクリーンへは `UseTexture(..., Target = true)`、MRT は `Targets`、深度のみは `DepthTarget` を使う。
- `Bindings.Set(name, value)` にはバッファ・テクスチャ・数値 (`float` / `Vec2`〜`Vec4` / `Mat4` / `List<float>` / `float[]`)・`FrameBuffer` 範囲を渡せる。予約名は `indices` (indexed draw) のみ。
- uniform はシェーダが宣言した全フィールドと名前・要素数がちょうど一致する必要がある (欠落・余分・サイズ不一致はエラー。前の draw の値は引き継がない)。フィールド名は 31 文字まで。
- `Bindings` は `Draw` 時点の中身でスナップショットされる。同じインスタンスを使い回すほうが速く、不要な名前を外すには `Clear()` する。
- `DrawOpts` の既定は blend なし / `Cull.Back` / `Primitive.Triangles` / `Depth = true`。
- `Blend` は値型 (struct) で、よく使う組は `Blends.Alpha` / `Additive` / `Multiply` にある。すべてのグラフィックスバックエンドで一致する組み合わせのみ受け付ける (alpha 係数は `Zero` / `One` / `*Alpha`、`Min` / `Max` の係数は `One`)。
- コンピュートは `UseShaderCompute` + `Dispatch`。`RWTexture2D<float4>` は `Rgba32f`、`RWTexture2D<float>` は `R32f` (他形式は `[format("rgba16f")]` のように明示)。

## 毎フレーム変わるデータ (`FrameBuffer`)

1 フレーム内で draw ごとに内容が変わるデータは `key` 宣言ではなく `FrameBuffer` を使う:

```csharp
foreach (var batch in batches)
{
    bindings.Set("insts", Gfx.FrameBuffer(Gfx.BufferType.Storage, batch.Floats));
    Gfx.Draw(4, bindings, opts, batch.Count);
}
```

- 呼ぶたびに新しい範囲を返し、そのフレームの間だけ有効 (前フレームの範囲が残った `Bindings` はエラー)。
- 読み取り専用の `StructuredBuffer` と `indices` に使える (コンピュート書き込み先には `UseBufferEmpty` を使う)。
- 1 回のサイズ上限は 128 MiB。大きいデータでは `float[]` と個数を渡す `FrameBufferArray` / `UseBufferArray` が速い。
- 手書きの静的頂点列は C# の `List<float>` にして `version` 省略の `UseBuffer` で渡すと、書き換えがホットリロードで即反映される。

## 読み戻し (`Readback`) とピック (`Pick`)

- `Gfx.Readback(key)` のキューに `Gfx.ReadTexture` で積み、毎フレーム poll して結果を受け取る。完了フレームは既定では GPU 完了次第だが、`ConfigOpts.ReadbackFixedLatency = true` で常に 2 フレーム後に固定できる。
- `Gfx.Pick(target, x, y)` は指定画素に一番手前で描いた draw を次フレームで調べ、`Gfx.PickResult()` に `PickHit` (`Draw` 番号と `Site` 呼び出し位置) を返す。fragment の `discard` や透明は考慮せず、Slang を持たないプレイヤー (Web 等) では `Draw = -1` になる。

## Slang シェーダの規約

シェーダは [Slang](https://shader-slang.org/) で書き、文字列のまま `UseShader` に渡す。どのグラフィックスバックエンドでも同じ結果にするため、以下の規約を守る:

### 頂点入力 (vertex pulling)

頂点入力レイアウトは持たず、頂点シェーダが `StructuredBuffer<V>` から `LUB_VERTEX_ID` (インスタンシングは `LUB_INSTANCE_ID`) で読む:

```slang
struct V { float3 pos; float pad0; float4 col; };
StructuredBuffer<V> verts;
struct VSOut { float4 col : COLOR0; float4 pos : SV_Position; };

[shader("vertex")] VSOut vs_main(uint vid : LUB_VERTEX_ID) {
  V v = verts[vid];
  VSOut o;
  o.pos = mul(mvp, float4(v.pos, 1.0));
  o.col = v.col;
  return o;
}
```

### 構造体レイアウトと varying

- `StructuredBuffer<T>` の要素 `T` は、`float3` の直後に `float` を置き、構造体サイズを `float3`/`float4` を含むなら 16 バイト倍数、`float2` までなら 8 バイト倍数にする (`Io.Interleave*` や `Lubx.Shapes` はこの規約で頂点列を返す)。
- Metal 互換のため、`verts[i].nrm` を直接演算せず `V v = verts[i];` とローカル変数に受けてから使う。
- VS→FS の varying は、FS 入力に VS 出力の先頭から使う分だけ同じ順で並べ、VS 出力の `SV_Position` は末尾に置く。

| ヘルパ | float / 頂点 | 構造体レイアウト |
| --- | --- | --- |
| `Io.InterleavePn` | 8 | `float3 pos; float pad0; float3 nrm; float pad1;` |
| `Io.InterleavePnu` | 12 | `pn` + `float2 uv; float2 pad2;` |
| `Io.InterleavePnut` | 16 | `pnu` + `float4 tangent;` |
| `Io.InterleavePncm` | 16 | `pn` + `float3 albedo; float pad2; float2 mr; float2 pad3;` |
| `Io.InterleavePncmw` | 20 | `pncm` + `float4 skin;` (`j0, w0, j1, w1`) |
| `Lubx.Shapes` | 12 | `pn` + `float4 color;` |

### テクスチャとサンプラ

```slang
LUB_TEXTURE2D(albedo);
struct FSIn { float2 uv : TEXCOORD0; };
[shader("fragment")] float4 fs_main(FSIn i) : SV_Target {
  return LUB_SAMPLE(albedo, i.uv);
}
```

- `LUB_TEXTURE2D(name)` でテクスチャとサンプラの組を宣言し、`LUB_SAMPLE(name, uv)` で読む。
- 分岐やループの後でサンプリングするときは明示 LOD の `LUB_SAMPLE_LOD(name, uv)` を使う (WebGPU が非一様制御フローでの暗黙 LOD を拒否するため)。
- `Texture2D tex; SamplerState tex_sampler;` のように別宣言することもできるが、サンプラ名はテクスチャ名で始める (複数テクスチャでのサンプラ共有やテクスチャ配列はエラー)。
- 1 シェーダ (VS + FS 合計) の上限はテクスチャ 8 枚、`StructuredBuffer` / `RWStructuredBuffer` 4 つ、`RWTexture2D` 4 枚。
- Web (WGSL) ではベクトル式への単項 `-` (`exp(-tau)`) が変換時に括弧を失うことがあるため、`exp(tau * -1.0)` のように `-1.0` を掛けて書く (スカラーの `-x` はそのままでよい)。
