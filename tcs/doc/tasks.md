# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

2026-07-12 の棚卸しで、lub (`../lub`) の Haxe 代替検証を P0 に設定した。
実装より先にギャップ分析 (T125) を置き、tcs 本体に足す機能と stub/shim で
吸収するものを切り分けてから着手する。

1. **T125: lub script 層のギャップ分析**
2. **T126: 00_hello 相当を tcs で動かす**
3. **T127: lub 上の hot reload 検証**

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: lub Haxe 代替検証 (dogfooding)

lub は C/C++ runtime + Lua 5.5 の上に Haxe → Lua transpile の script 層を持つ。
この script 層を tcs で置き換えられるかを検証する。

### T125: lub script 層のギャップ分析
- 目的: Haxe の代わりに tcs で lub の script 層を書けるかの判断材料を揃える
- 作業:
  - boot 契約 (module が table を return し `onInit`/`onEvent`/`onFrame`/`onQuit` を公開) と tcs 出力形式の突き合わせ
  - `@:native` snake_case リネーム相当 (`Gfx.beginPass` → `Gfx.begin_pass`) の要否と実現方式 (tcs 機能 / stub 命名で吸収)
  - `PassOpts` 等の匿名オプション table の C# 表現 (class + object initializer → Lua table リテラル)
  - opaque handle (Haxe `Dynamic` 相当) と Lua multi-return の表現
  - lubx 層 (Haxe 実装のヘルパー) の扱い。PoC では core API 直呼びで回避する前提を確認
- 完了条件: ギャップ一覧と「tcs に足す機能 / stub・shim で吸収 / lub へ feature request」の切り分けが文書化されている

### T126: 00_hello 相当を tcs で動かす
- 目的: 最小サンプルで tcs → lub runtime の E2E 経路を成立させる
- 作業:
  - lub core API の最小 C# 参照 stub (`Gfx` / print 相当) を tcs 側に置く
  - エントリ契約 (table return + callbacks) への対応 (T125 の方針に従う)
  - tcs 出力 Lua を lub runtime にロードして clear 画面を出す
- 完了条件: tcs で書いた 00_hello 相当が lub 上で動く

### T127: lub 上の hot reload 検証
- 目的: lub の hot reload と tcs 出力 Lua の相性を確認する
- 作業:
  - lub の reload 経路 (lume.hotswap) が tcs 出力 module で機能するか検証する
  - tcs 側 HotReload runtime との重複・競合を整理する
- 完了条件: tcs 出力 module の編集 → reload で lub の画面が更新される

T126 の結果を見てから、breakout 級サンプル移植などの後続タスクを切る。

---

## P2: 整備

### T123: tcs analyzer package の release 手順整備 (縮小)
- 目的: analyzer nupkg を再生成・検証できる手順を残す
- 縮小理由: NuGet 公開は当面やらない (Q8)。local nupkg consumer gate は run-tests で恒常化済みのため、残りは手順の文書化のみ
- 作業: release 手順 (version bump / pack / consumer 検証) を README に追加する
- 完了条件: README の手順どおりに local nupkg を再生成・検証できる
