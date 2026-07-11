# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

lub Haxe 代替検証は G1〜G5 のギャップ解消と 00_hello 相当の実機動作・hot reload
検証まで完了した (`doc/lub-gap-analysis.md`)。次は breakout 級サンプル移植で
未実測項目 (ref 型 instance method、event field 透過) と実用性を検証する。

1. **T132: lub breakout 級サンプル移植**

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: lub Haxe 代替検証 (dogfooding)

lub は C/C++ runtime + Lua 5.5 の上に Haxe → Lua transpile の script 層を持つ。
この script 層を tcs で置き換えられるかを検証する。契約とギャップの詳細は
`doc/lub-gap-analysis.md` を参照。

### T132: lub breakout 級サンプル移植
- 目的: 実用規模のゲームループ (入力・リソースロード・描画) で tcs → lub 経路の破綻がないか検証する
- 作業:
  - lub の 09_breakout または 17_flappy 相当を TinyC# で書く
  - lub_stub.cs / lub_shim.lua を必要 API (Input / Io / Gfx.draw / use_shader / use_buffer) まで拡張する
  - 未実測項目を実測する: ref 型 instance method の emit 形 (colon call と lub 規約の整合)、`onEvent(e)` の field 名透過
- 完了条件: 移植サンプルが lub 上でプレイ可能に動き、`--capture` で描画を確認できる

