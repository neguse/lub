# ドキュメント方針

lub のドキュメントは「現状ドキュメント」と「記録」の 2 種類に分け、**ディレクトリで区別する**。
ファイルの置き場所を見れば扱いが分かる状態を保つ。

## 現状ドキュメント — root と `docs/` 直下

今のリポジトリの姿を記述する。実装と食い違ったら直す。

- 置き場所: リポジトリ root(`README.md` / `AGENTS.md` / `tasks.md`)、`docs/` 直下、
  `web/README.md` のような生きているサブプロジェクトの README。
- 挙動を変えるコミットでは、その挙動を記述している現状ドキュメントを同じコミットで直す。

## 記録 — `docs/log/`・完了した機能ディレクトリ

当時の調査・設計・計画のスナップショット。**本文は書き換えない**(リンク切れ・typo 修正のみ可)。

- 置き場所:
  - `docs/log/` — 単発の設計・調査・計画記録。ファイル名は `YYYY-MM-DD-<slug>.md`。
  - 完了した機能ディレクトリ(例: `haxe-wasm/`)— その機能の調査・引き継ぎ記録一式。
- 先頭に `> 記録:` で始まる引用行を置き、いつ時点の何か・現状はどこを見るかを書く。
- 古い記述に気づいても本文は直さず、前書きの参照先だけを更新する。

## 設計ドキュメントの寿命

実装前に書いた設計ドキュメントは、実装が終わったらどちらかにする。中間状態で放置しない。

- **現状ドキュメント化**: 現在の使い方・仕組みの記述に書き直し、`docs/` 直下に置く(例: `serve.md`)。
- **記録化**: 前書きを付けて `docs/log/` へ日付プレフィックスで移す
  (例: `log/2026-06-22-native-backend-design.md`)。

## 検査

`scripts/docs-lint.sh` が機械的に検査できる範囲(索引とファイルの整合、
`docs/log/` の命名・バナー規約)を commit フックと CI で確認する。

## 索引

現状ドキュメント (`docs/` 直下):

- [design.md](design.md) — lub の why / to-be / 設計原則
- [roadmap.md](roadmap.md) — phase ごとの達成目標と状態
- [manual/](manual/) — 利用者向けガイド(web の `/docs.html` に描画される)
- [api-glue.md](api-glue.md) — 多言語 (Lua/Haxe/C#) binding の構成
- [serve.md](serve.md) — `--serve` Web 開発モード
- [dx12-backend.md](dx12-backend.md) — D3D12 native backend の構成
- [profile.md](profile.md) — 組み込み CPU profiler
- [release-build.md](release-build.md) — Release build 手順
- [sprites-bench.md](sprites-bench.md) — sprite benchmark

記録:

- [log/](log/) — 単発の設計・調査・計画記録
- [../haxe-wasm/](../haxe-wasm/) — Haxe compiler WASM 化の計画・実施記録・引き継ぎ
