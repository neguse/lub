# lub と lubx

API は 2 つのパッケージに分かれている。この境界は設計上の意図なので、
どちらを使うか・どちらに何を期待するかを知っておくと見通しが良くなる。

## lub — core primitive

`lub.*` は C runtime が Lua に公開する API への型付き extern。
runtime が所有しなければ一貫性を保てない primitive だけがここに入る:

- 起動 config と終了(`Lub`)
- GPU 実行と frame lifecycle(`Gfx`)
- 入力の frame 内 snapshot(`Input`)
- hot reload 前提のファイル I/O(`Io`)
- resource identity(key + version、reload の一貫性)
- 物理・音声など native ライブラリの境界(`Phys2d` / `Phys3d` / `Audio`)
- 診断情報(`Profiler`、`Sys`)

gameplay の意味論、シーン構造、アニメーションといった「ゲームの都合」は
core API にしない方針。既存エンジンとの API 互換も目標にしない。

## lubx — Haxe ライブラリ層

`lubx.*` は `lub.*` の上に 純粋な Haxe で書かれた便利層。
`Boot`(起動定型)、`SpriteBatch` / `Atlas` / `Shapes` / `Text`(2D 描画)、
`Renderer3d` / `Mesh3d` / `Shapes3d` / `Bones`(3D レンダラ。設計記録は `docs/log/2026-07-12-renderer3d-design.md`)、
`Camera2d` / `Camera3d`、`Assets` / `Png`、`Rand`、`Sfx` など。

runtime の機能ではないので、ソースを読めばすべて `lub.*` の呼び出しに
分解できる。挙動を変えたければ自分のプロジェクトに写して改造してよい
層であり、lubx に無い機能は同じやり方で自作できる。

## 使い分けの目安

- まず `lubx` で書き始める(`Boot.config`、`SpriteBatch`、`Text` など)
- 描画やリソースの挙動を細かく制御したくなったら `lub.Gfx` へ降りる
- 「この機能は無いのか?」と思ったら lubx のソースを読む — 同じ書き方で
  自分の層を作るのが lub の想定する拡張方法
