# lub と lubx

API は 2 つのパッケージに分かれている。この境界は設計上の意図なので、
どちらを使うか・どちらに何を期待するかを知っておくと見通しが良くなる。

## lub — core primitive

`Lub.*`(`cs-lib/lub_stub.cs`)は C runtime が公開する API の型付き宣言。
runtime が所有しなければ一貫性を保てない primitive だけがここに入る:

- 起動 config と終了(`App.Config` / `App.Quit`)
- GPU 実行と frame lifecycle(`Gfx`)
- 入力の frame 内 snapshot(`Input`)
- hot reload 前提のファイル I/O(`Io`)
- resource identity(key + version、reload の一貫性)
- 物理・音声など native ライブラリの境界(`Phys2d` / `Phys3d` / `Audio`)
- 診断情報(`Profiler`、`Sys`)

gameplay の意味論、シーン構造、アニメーションといった「ゲームの都合」は
core API にしない方針。既存エンジンとの API 互換も目標にしない。

## lubx — C# ライブラリ層

`lubx`(`cs-lib/lubx`、namespace `Lubx`)は `Lub.*` の上に純粋な C# で書かれた便利層。
`SpriteBatch` / `Atlas` / `Shapes` / `Text`(2D 描画)、
`Renderer3d` / `Mesh3d` / `Shapes3d` / `Bones`(3D レンダラ。設計記録は `docs/log/2026-07-12-renderer3d-design.md`)、
`Camera2d` / `Camera3d`、`Assets`、`Rand`、`Sfx` など。

runtime の機能ではないので、ソースを読めばすべて `Lub.*` の呼び出しに
分解できる。挙動を変えたければ自分のプロジェクトに写して改造してよい
層であり、lubx に無い機能は同じやり方で自作できる。

## key の付け方

resource の key はアプリ全体で 1 つの文字列で、フレームと編集をまたいで同じものを指す。
同じ key は同じもの、違う key は違うものなので、key で引く(`Gfx.LookupTexture` など)のも正当な使い方。
1 つのアプリに複数のゲームやモジュールを入れるときは、`game_a/floor` のように親のパスを `/` でつないで分ける。
エンジンに scope の仕組みは無く、パスはただの文字列の連接。

lubx で resource を宣言する部品(`SpriteBatch`、`Atlas`、`Text`、`MeshText`、`Mesh3d`、`Renderer3d`、`Assets`、`Sfx.Synth`)は、key を最初の引数に取る。
resource が 1 つの部品(`Atlas`、`Text`、`Assets`、`Sfx.Synth`)はその key に、複数の部品はその key の下(`new Renderer3d("game_a/ren")` なら `game_a/ren/hdr` など)に宣言する。

```csharp
var batch = new SpriteBatch("game_a/ui", 320, 180);
var ren = new Renderer3d("game_a/ren");
var floor = Assets.Shader("game_a/floor", vsPath, fsPath);
```

同じ key の部品は同じ resource を共有するので、中身の違うものに同じ key を渡さない。
`Sfx.Blip` / `Sfx.Noise` の音と `SpriteBatch.Rect` / `Disc` の atlas は、中身の決まった共通の key を使うので key を取らない。
`SpriteBatch` の shader も中身が決まっているので、key によらずすべての `SpriteBatch` で 1 つ(`lubx/sprite/instanced`)を共有する。

## SpriteBatch の重なり

`SpriteBatch` は、`Begin` から `Flush` までに積んだ絵を atlas ごとに 1 draw にまとめて描く。
そのため重なりは呼んだ順ではなく、`Begin` の後にその atlas が初めて出た順になる(同じ atlas の中は呼んだ順)。
`Text` の文字も、その font の atlas の 1 つとして同じ扱いになる。
違う atlas の絵を呼んだ順に重ねたいときは、重ねる層ごとに `Begin` から `Flush` までを分ける。

```csharp
batch.Begin();
batch.Quad(bg, src, 0, 0, 320, 180);    // 背景の atlas
batch.Flush();
batch.Begin();
batch.Quad(chara, src, 100, 80, 16, 16); // 背景の上に重ねる atlas
batch.Flush();
```

## Renderer3d のカメラ

`Renderer3d.Begin` に渡す `Camera` の `Fov`、`Near`、`Far` は、省くと 60 度、0.1、100 になる。
カメラから `Far` より遠い物は描かれないので、遠くの背景(太陽や星など)を置くときは `Far` を大きくする。

## 使い分けの目安

- まず `lubx` で書き始める(`SpriteBatch`、`Text` など)
- 描画やリソースの挙動を細かく制御したくなったら `Gfx` へ降りる
- 「この機能は無いのか?」と思ったら lubx のソースを読む — 同じ書き方で
  自分の層を作るのが lub の想定する拡張方法
