# 描画モデル

`Gfx` は即時モードの GPU API。毎フレーム「何を使い、何を描くか」を宣言し、
リソースの寿命管理は runtime に任せる。

## use* — key + version によるリソース宣言

```haxe
var vs = Io.loadText("data/cube.vs.slang");
var fs = Io.loadText("data/cube.fs.slang");
if (vs.text == null || fs.text == null) return;
var shader = Gfx.useShader("cube", vs.text, fs.text, vs.version * 31 + fs.version);
```

- `use*` 系は毎フレーム同じ `key` で呼ぶ。`version` が前フレームと同じなら
  キャッシュが返り、変わっていれば作り直される。
- `version` にはコンテンツハッシュを渡す。`Io.load*` の返す `version` を
  そのまま使えばよい。複数ファイルを 1 リソースに束ねるときは
  `a.version * 31 + b.version` のような順序依存の結合を使う(XOR は
  同一内容や入れ替えで打ち消し合う)。
- 手続き生成データのように「内容を変更した時点」が明確なら、その時点でだけ
  `Gfx.nextVersion()` を呼び、返った revision を `version` に使う。この値は
  GPU resource cache と同じ寿命を持ち、entry の hot reload を跨いで単調増加
  する。毎フレーム呼ぶ必要はない。
- `use*` されなくなったリソースは数フレーム後に自動破棄される
  (`Lub.config` の `resource_sweep_after_frames`)。

このモデルにより、シェーダファイルを保存した瞬間に version が変わって
リソースが作り直される = **アセットの hot reload がコードと同じ仕組みで動く**。

シェーダソースは [Slang](https://shader-slang.org/) で書き、文字列のまま
`useShader(key, vs, fs, version)` に渡す(native / web 共通)。

VS→FS の varying には 2 つの規約がある:

- FS の入力構造体は、VS 出力の varying を **先頭から使う分だけ** 同じ順で
  宣言する(途中を飛ばさない)。
- VS 出力構造体の `SV_Position` メンバは **最後に置く**。D3D12 (`native`
  backend) はステージ間をレジスタ位置で一致させるため、`SV_Position` が
  先頭にあると FS 側の varying 位置がずれる。Vulkan / WebGPU では
  `SV_Position` は location 採番の対象外なので、この順序はどの backend
  でも同じ意味になる。

## pass と draw

```haxe
Gfx.beginPass({target: Gfx.mainTex,
	clear_color: lua.Table.fromArray([0.05, 0.05, 0.15, 1.0])});
Gfx.draw(36, {verts: buf, uniforms: {mvp: lua.Table.fromArray(mvp.m)}},
	{shader: shader});
Gfx.endPass();
```

- 描画は `beginPass` / `endPass` で囲む。画面へ描くなら `target: Gfx.mainTex`、
  offscreen へ描くなら `useTexture` で `{target: true}` を付けて作った
  テクスチャを渡す。MRT は `targets`、depth-only は `depth_target`(詳細は
  `PassOpts`)。
- `draw(count, bindings, opts)` の `bindings` はシェーダ依存の自由なテーブル。
  予約名は `indices`(indexed draw)、`instances`(インスタンシング)、
  `uniforms` の 3 つ。それ以外のバッファ値は頂点バッファ、テクスチャ値は
  キー名でシェーダのテクスチャに束縛される。
- `opts`(`DrawOpts`)の既定値は blend=NONE / cull=BACK /
  primitive=TRIANGLES / depth=true。

compute は `useShaderCompute` + `dispatch`、GPU からの読み戻しは
`readback` を参照。

## 定型: ready になるまでスキップ

web ではファイル取得が非同期なので、`Io.load*` は ready になるまで本体が
null を返す。null の間はそのフレームの処理をスキップするのが定型:

```haxe
var verts = Io.loadFloats("data/cube.verts.lua");
if (verts.data == null) return; // pending or error
```

この「毎フレーム宣言して、揃うまで待つ」スタイルにより、初期化順や
ロード完了イベントを管理するコードが不要になる。
