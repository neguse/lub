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
