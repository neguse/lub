# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

lub Haxe 代替検証のギャップ分析は `doc/lub-gap-analysis.md` (T125) にまとめた。
G1〜G4 のギャップを tcs 側機能として埋めてから PoC (T126/T127) へ進む。

1. **T128: object initializer の黙殺修正** (G1 — lub 非依存の正しさバグでもある)
2. **T129: entry class 指定で module return を出す** (G2)
3. **T130: naming warning の抑制手段** (G3)
4. **T126: 00_hello 相当を tcs で動かす**
5. **T127: lub 上の hot reload 検証**
6. **T131: Lua multi-return 対応** (G4 — breakout 級サンプルの前提)

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: lub Haxe 代替検証 (dogfooding)

lub は C/C++ runtime + Lua 5.5 の上に Haxe → Lua transpile の script 層を持つ。
この script 層を tcs で置き換えられるかを検証する。契約とギャップの詳細は
`doc/lub-gap-analysis.md` を参照。

### T128: object initializer の黙殺修正 (G1)
- 目的: `new T { ... }` の初期化子が診断なしで消える既存バグを直す (T97 方針)
- 作業:
  - Lua 出力対象外の型 (`--ref` 型) の `new` + initializer は plain table literal `{ key = value, ... }` へ emit する
  - Lua 出力対象の通常 class は `.new()` + field 代入で対応するか、TCS1001 診断にする
  - analyzer / `tcs check` / transpiler warning の判定を共有 facts で揃える
- 完了条件: initializer が黙って消えるケースがなく、`--ref` 型の opts が Lua table として渡る

### T129: entry class 指定で module return を出す (G2)
- 目的: lub の entry module 契約 (require が table を返す) に tcs 出力を適合させる
- 作業: CLI に entry class 指定 (例: `--entry Hello`) を追加し、出力末尾に `return Hello` を出す
- 完了条件: 生成 Lua を require すると指定 class の table が返る

### T130: naming warning の抑制手段 (G3)
- 目的: lub の wire format (lowerCamel / snake_case) を使うコードを `tcs check` ゲートに乗せられるようにする
- 作業: naming warning を抑制する CLI flag 等を追加する (デフォルト挙動は変えない)
- 完了条件: lowerCamel callback を含むソースで `tcs check` が exit 0 にできる

### T126: 00_hello 相当を tcs で動かす
- 目的: 最小サンプルで tcs → lub runtime の E2E 経路を成立させる (T128〜T130 が前提)
- 作業:
  - lub core API の最小 C# stub (`Gfx` / `Lub` / `os` 相当) を `samples/lub/` に置く
  - `lubx.Boot.config` 相当の起動定型 (env 読み + `Lub.config`) を C# で書く
  - tcs 出力 Lua を lub runtime にロードして clear 画面を出す
  - 未確認事項 (ref 型 instance method の emit 形、event field 透過、`os.getenv` 透過) を実測する
- 完了条件: tcs で書いた 00_hello 相当が lub 上で動く

### T127: lub 上の hot reload 検証
- 目的: lub の hot reload と tcs 出力 Lua の相性を確認する
- 作業:
  - `tcs --watch` で entry .lua を再生成し、lub 側の mtime poll → lume.hotswap が機能するか検証する
  - tcs 側 HotReload runtime との重複・競合を整理する
- 完了条件: C# ソースの編集 → lub の画面が再起動なしで更新される

### T131: Lua multi-return 対応 (G4)
- 目的: `Io.loadText` (text/version/status/error) など multi-return API を C# から使えるようにする
- 作業: `out` 引数への割り当てなど、multi-return の C# 表現を設計して実装する
- 完了条件: multi-return を返す `--ref` stub API を C# から受け取れる
- 備考: 00_hello では不要。breakout 級サンプル移植の前提

T126/T127 の結果を見てから、breakout 級サンプル移植などの後続タスクを切る。

---

## P2: 整備

### T123: tcs analyzer package の release 手順整備 (縮小)
- 目的: analyzer nupkg を再生成・検証できる手順を残す
- 縮小理由: NuGet 公開は当面やらない (Q8)。local nupkg consumer gate は run-tests で恒常化済みのため、残りは手順の文書化のみ
- 作業: release 手順 (version bump / pack / consumer 検証) を README に追加する
- 完了条件: README の手順どおりに local nupkg を再生成・検証できる
