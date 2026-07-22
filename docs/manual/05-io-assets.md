# ファイル入力とアセット

## Io.load* — 毎フレーム呼べるファイル入力

`Io.loadText` / `loadFloats` / `loadGltf` は hot reload 前提の即時モード API。
mtime の fast-path + コンテンツハッシュにより、毎フレーム呼んでも安い。

```haxe
var r = Io.loadText("samples/mygame/data/config.txt");
if (r.text == null) return; // ready になるまで待つ
```

戻り値は共通パターン(Lua multi-return):

| フィールド | 意味 |
| --- | --- |
| `text` / `data` / `mesh` | 本体。ready になるまで null |
| `version` | 内容の FNV-1a ハッシュ。`Gfx.use*` の version にそのまま渡せる |
| `status` | `"ready"` / `"pending"` / `"error"`。native は pending にならない |
| `error` | status が error のときの理由 |

web ではファイル取得が非同期なので `"pending"` があり得る。null チェックで
そのフレームをスキップすれば、native / web 両対応になる。

## パスの規約

パスは 起動時の cwd 基準。サンプルはリポジトリルートから
`lub samples/<name>/<name>.hxml` のように起動するので、コード内のパスも
`samples/<name>/data/...` と書く。

## ファイル形式

- `loadText`: 任意のテキスト(シェーダソース、設定など)
- `loadFloats`: `return { 1.0, 2.0, ... }` 形式の Lua ファイルを Float 配列に
- `loadGltf`: glTF (.gltf / .glb)。結果は `Io.interleavePn` 等で頂点列にして
  `Gfx.useBuffer` へ

PNG 画像は `lubx.Png`、TTF フォントは `lub.Font` / `lubx.Text`、音声は
`lub.Audio` / `lubx.Sfx` を参照。
