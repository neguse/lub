# タスクリスト

コード・テスト・ドキュメント・runtime・samples を分担して棚卸しした結果。
完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

このファイルはタスク番号順に実行しない。
Compact C# baseline は T103/T115 で定義済み。
以後は Core / Useful / Out of scope の判断に沿って実装・サンプル検証へ進む。

1. **T123: tcs analyzer package を正式導線にする**
2. **T124: tcs check / analyzer / transpiler diagnostics の一致を継続検証する**

---

## Phase 1: ユースケース検証 (完了)

T7-T11 は完了済み。サンプルは当初の「手書き期待 Lua 出力」ではなく、
現在のテスト方針に合わせて **C# サンプル → トランスパイル → Lua 5.5 実行** で検証している。

---

## P1: 仕様ギャップ・実用上の不足

### T123: tcs analyzer package を正式導線にする
- 目的: Rider / dotnet build / CI で同じ tcs 準拠診断を得られる package 導線を整える
- 作業:
  - analyzer package metadata / README / versioning policy を整える
  - PackageReference sample を維持する
  - `run-tests` の package consumer gate を正式 CI gate とする
  - release 手順を README に追加する
- 完了条件: local nupkg consumer と Rider 実機で TCS1001/TCS1002/TCS1003 の severity が一致する

### T124: tcs check / analyzer / transpiler diagnostics の一致を継続検証する
- 目的: shared compliance facts の変更時に IDE/build/check/transpile の判定ずれを防ぐ
- 作業:
  - TCS1003 を含む analyzer-demo fixture を維持する
  - CLI fixture と analyzer test の expected diagnostics を同じケースで比較する
  - support matrix の診断対象と test case を同期する
- 完了条件: 代表 unsupported syntax / API / collection null が analyzer, `tcs check`, transpiler warning で同じ ID になる
