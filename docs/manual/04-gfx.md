# 描画モデル

`Gfx` は即時モードの GPU API。毎フレーム「何を使い、何を描くか」を宣言し、
リソースの寿命管理は runtime に任せる。

## use* — key + version によるリソース宣言

```csharp
Io.LoadText("data/cube.vs.slang", out var vs, out var vsVersion, out _, out _);
Io.LoadText("data/cube.fs.slang", out var fs, out var fsVersion, out _, out _);
if (vs == null || fs == null) return;
var shader = Gfx.UseShader("cube", vs, fs, vsVersion * 31 + fsVersion);
```

- `use*` 系は毎フレーム同じ `key` で呼ぶ。`version` が前フレームと同じなら
  キャッシュが返り、変わっていれば作り直される。
- `version` は key の内容に対する同一性の主張。渡してよいのは内容から
  導ける値だけ — ファイルは `Io.load*` の返す `version`(content hash)を
  そのまま、不変内容は定数、複数ファイルを 1 リソースに束ねるときは
  `a.version * 31 + b.version` のような順序依存の結合(XOR は同一内容や
  入れ替えで打ち消し合う)。
- 内容から導けない(手続き生成などで「変更履歴」が要る)場合は `version` を
  省略する。省略は「内容が変わった」宣言で、runtime が新しい実効 version
  を発行して必ず upload する。毎フレーム use する key で upload を避けたい
  ときは、前回の戻り値 ref の `version` を渡して「変わっていない」を再主張
  する(`lubx.Atlas` がこの形)。
- 守るべき不変条件は一つ: 同じ key の異なる内容に同じ version を再利用
  しない — hot reload を跨いでも。これを保証できるなら値の作り方は自由
  (自前 counter でも mtime でも構わない)。ただし素朴な static / instance
  counter は reload で初期値に巻き戻ってこの保証を破り、cache に残った値
  との偶然の一致で更新が黙って skip される(「ライフサイクル」章参照)。
  保証を自分で持ちたくなければ省略(変更宣言)に任せる。同じ key で方式
  (定数 / 省略 / hash)を混ぜない。
- `use*` されなくなったリソースは数フレーム後に自動破棄される
  (`Config` の `ResourceSweepAfterFrames`)。

このモデルにより、シェーダファイルを保存した瞬間に version が変わって
リソースが作り直される = アセットの hot reload がコードと同じ仕組みで動く。

シェーダソースは [Slang](https://shader-slang.org/) で書き、文字列のまま
`UseShader(key, vs, fs, version)` に渡す(native / web 共通)。

VS→FS の varying には 2 つの規約がある:

- FS の入力構造体は、VS 出力の varying を 先頭から使う分だけ 同じ順で
  宣言する(途中を飛ばさない)。
- VS 出力構造体の `SV_Position` メンバは 最後に置く。D3D12 (`native`
  backend) はステージ間をレジスタ位置で一致させるため、`SV_Position` が
  先頭にあると FS 側の varying 位置がずれる。Vulkan / WebGPU では
  `SV_Position` は location 採番の対象外なので、この順序はどの backend
  でも同じ意味になる。

## pass と draw

```csharp
Gfx.BeginPass(new PassOpts
{
    Target = Gfx.MainTex,
    ClearColor = new double[] { 0.05, 0.05, 0.15, 1.0 },
});
Gfx.Draw(36,
    new Dictionary<string, object>
    {
        ["verts"] = buf,
        ["uniforms"] = new Dictionary<string, object> { ["mvp"] = mvp.M },
    },
    new DrawOpts { Shader = shader });
Gfx.EndPass();
```

- 描画は `BeginPass` / `EndPass` で囲む。画面へ描くなら `Target = Gfx.MainTex`、
  offscreen へ描くなら `UseTexture` で `Target = true` を付けて作った
  テクスチャを渡す。MRT は `Targets`、depth-only は `DepthTarget`(詳細は
  `PassOpts`)。
- `Draw(count, bindings, opts)` の `bindings` はシェーダ依存の自由なテーブル。
  予約名は `indices`(indexed draw)、`instances`(インスタンシング)、
  `uniforms` の 3 つ。それ以外のバッファ値は頂点バッファ、テクスチャ値は
  キー名でシェーダのテクスチャに束縛される。
- `opts`(`DrawOpts`)の既定値は blend=NONE / cull=BACK /
  primitive=TRIANGLES / depth=true。

compute は `UseShaderCompute` + `Dispatch`、GPU からの読み戻しは
`Readback` を参照。

## 定型: ready になるまでスキップ

web ではファイル取得が非同期なので、`Io.Load*` は ready になるまで本体が
null を返す。null の間はそのフレームの処理をスキップするのが定型:

```csharp
Io.LoadFloats("data/cube.verts.lua", out var verts, out _, out _, out _);
if (verts == null) return; // pending or error
```

この「毎フレーム宣言して、揃うまで待つ」スタイルにより、初期化順や
ロード完了イベントを管理するコードが不要になる。
