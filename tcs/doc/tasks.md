# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

lub Haxe 代替検証は breakout 級サンプルの実機動作まで完了した
(`doc/lub-gap-analysis.md`)。以降のサンプル移植・Useful 層追加は需要駆動で切る。

1. **T133: 完全修飾 API アクセスの TCS1002 誤検出修正**

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: lub Haxe 代替検証 (dogfooding)

lub は C/C++ runtime + Lua 5.5 の上に Haxe → Lua transpile の script 層を持つ。
この script 層を tcs で置き換えられるかを検証する。契約とギャップの詳細は
`doc/lub-gap-analysis.md` を参照。

### T133: 完全修飾 API アクセスの TCS1002 誤検出修正
- 目的: `System.Math.Min(...)` のような完全修飾アクセスで `System.Math` の部分式が TCS1002 (unsupported API) に誤検出されるのを直す
- 作業:
  - TryGetUnsupportedApi の member access 判定で、supported 型そのものを指す qualified name を除外する
  - analyzer / `tcs check` / transpiler warning の共有 facts で同じ判定にする
- 完了条件: `System.Math.Min(1, 2)` が診断なしで通り、`System.IO.File.ReadAllText` は引き続き TCS1002 になる
