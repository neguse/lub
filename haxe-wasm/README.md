# haxe-wasm — Haxe コンパイラの WASM ビルド

web playground がブラウザ内で使う Haxe→Lua コンパイラ(wasm_of_ocaml 製)を
ビルドする一式。成果物は `cd web && npm run gen-haxe` が
`web/public/haxe-wasm/` に固める(配線は [../web/README.md](../web/README.md))。

## ビルド

```
bash haxe-wasm/build.sh
```

Docker(`Dockerfile`)で Haxe `5.0.0-preview.1` に `patches/` を適用して
wasm 化し、`haxe.js` + `haxe.assets/*.wasm` + `std/` を `dist/` に抽出、
`00_hello` を native golden と byte 比較して検証する。
反復開発は `bash harness/iter.sh`(ビルド済み `build/` に対する再検証)。

## 構成

- `build.sh` / `Dockerfile` — 全自動ビルド + 成果物抽出 + 検証
- `patches/` — Haxe 本体と opam ライブラリの wasm 化パッチ一式
- `scripts/` — ビルド各段のスクリプトとパッチ適用
- `harness/` — Node / ブラウザでの実行・トレース・検証ハーネス
- `results/` — 調査結果(missing primitives 一覧等)
- `LICENSE` — この配下の成果物は GPL-2.0-or-later(root README のライセンス節参照)
- `build/`・`dist/` — 生成物(gitignore)

## 記録

- [plan.md](plan.md) — 実現性検証の計画(2026-05)
- [report.md](report.md) — 実施記録(2026-06-02、native golden と byte 一致・GO 確定)
- [HANDOFF.md](HANDOFF.md) — 引き継ぎ(web playground 統合まで完了)
