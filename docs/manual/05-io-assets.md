# ファイル入力とアセット

## Io.Load* — 毎フレーム呼べるファイル入力

`Io.LoadText` / `LoadFloats` / `LoadGltf` は hot reload 前提の即時モード API。
mtime の fast-path + コンテンツハッシュにより、毎フレーム呼んでも安い。

```csharp
Io.LoadText("samples/mygame/data/config.txt", out var text, out var version, out var status, out var error);
if (text == null) return; // ready になるまで待つ
```

戻り値は共通パターン(`out` 引数。Lua では multi-return):

| 値 | 意味 |
| --- | --- |
| `text` / `data` / `mesh` | 本体。ready になるまで null |
| `version` | 内容の FNV-1a ハッシュ。`Gfx.Use*` の version にそのまま渡せる |
| `status` | `Io.Status.Ready` / `Pending` / `Error`(Lua では `"ready"` 等の文字列)。native は pending にならない |
| `error` | status が error のときの理由 |

web ではファイル取得が非同期なので `"pending"` があり得る。null チェックで
そのフレームをスキップすれば、native / web 両対応になる。

## パスの規約

パスは 起動時の cwd 基準。サンプルはリポジトリルートから
`lub samples/<name>/<Entry>.csproj` のように起動するので、コード内のパスも
`samples/<name>/data/...` と書く。

## ファイル形式

- `LoadText`: 任意のテキスト(シェーダソース、設定など)
- `LoadFloats`: `return { 1.0, 2.0, ... }` 形式の Lua ファイルを float 配列に
- `LoadGltf`: glTF (.gltf / .glb)。結果は `Io.InterleavePn` 等で頂点列にして
  `Gfx.UseBuffer` へ

PNG 画像は `Png`、TTF フォントは `Font` / `lubx.Text`、音声は
`Audio` / `lubx.Sfx` を参照。

## lubx のアセット定型

`Assets` は読み込みと resource の宣言を 1 行にする。どれも毎フレーム呼んで宣言し続け、未 ready の間は null を返す。

- `Assets.Shader(key, vsPath, fsPath)`: vs / fs を読んで `Gfx.UseShader`
- `Assets.Floats(key, usage, path)`: `LoadFloats` + `Gfx.UseBuffer`
- `Assets.Wav(key, path)`: `LoadBytes` + `Audio.Decode` + `Audio.SndBytes`。snd handle を返す。decode は version が変わったときだけで、以降は再宣言だけ。`ResourceSweepAfterFrames` を超えて呼ばないと snd が sweep され、次の呼び出しは error になる
- `Assets.RenderTarget(key, w, h, fmt)`: render target の `Gfx.UseTexture`。filter は既定 Linear、wrap は既定 Clamp。サイズが変わると作り直される

`Sfx.Synth(key, dur, version, sample)` は波形の式を自分で書く合成音の枠。
`sample(t, u)` は秒 t と進行度 u (0 から 1) を受けて 1 sample を返し、-1 から 1 に丸められる。
LP フィルタなどの状態は、`sample` が捕まえた変数に持つ。
key と version で波形を cache するので、式を変えたら version を上げる。
