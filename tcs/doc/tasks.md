# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## IL トラック（`doc/il-design.md` M1-M5 の実施）

価値最大化の原則:
1. **情報を先に買う** — 2 大不確実性（release object model / IL 抽象度）を
   コンパイラのコードを書く前に実測 (perf) と紙の演習で決着させる
2. **各段階で独立に価値を着地させる** — 途中で止まっても損しない順序
3. **数値モデルの凍結を C backend 着手より前に** — double 前提で
   IL→C を書く手戻りを封じる

### Phase 0 — 情報を買う + 発見バグ修正（並行可）

### Phase 1 — M1: 挙動不変の内部再編 ✓ (T214a-c 完、done.md 参照)

- [x] T224 完 (2026-07-18): IlExport 契約完備、fallback 構文の整理
      (static method group IL 化 / instance method group・定数式 alignment
      診断化 / lock・using 等は既存診断)。legacy visitor は診断出力と
      挙動不変の保険として恒久保持


### Phase 3 — 価値の刈り取り

- [x] T218 完 (2026-07-18): M3 IL→C backend (tcs2c)。継承 / Dict / closure /
      ctor 連鎖 / 静的 link (--lib) まで全マイルストーン受入済み
      (digest 3/3 + 全サンプル stdout 一致)。未対応構文は明示エラー方針で、
      対応面の拡張は実利用の需要駆動 (done.md 第一〜第八参照)
- T219b 完 (done.md 参照): struct / record struct の値セマンティクス
  対応一式。設計方針の正本は support-matrix / CLAUDE.md / il-spec §10
- [ ] **T220** (P1、ゲート解除 2026-07-18): hot reload の実装。ユーザー判断で
      「cold reload 安全弁止まり」を却下し、il-design §6 の CLOS 流 eager
      migration を実装対象とする。検証面は同一 VM 内で 2 版を transpile して
      reload するセマンティックテストで立てる (lub 側導線は待たない)。段階:
      - [x] (a) layout hash の struct 推移展開 (IlExport — struct 内部変更が
            owner class の hash へ伝播。T219b の struct 型 field 解禁に先行)
      - [x] (b) reload runtime: weak registry + metadata diff 適用
            (added=initializer / discarded=破棄 / retained 保持、in-place で
            identity 維持)、OnReload フック、reload は frame 境界
      - [x] (c) struct 値の再直列化 migration (owner walk 経由、il-design §6)
      - 残: 実導線 (ファイル監視 → EmitReloadChunk → 実行中 VM へ適用) は
        実利用トラックで接続。List/Dict 内 struct 値の再直列化と record class
        の migration は需要待ち。record struct の IlExport (layout hash /
        migration) も未対応 — 需要待ち

---

## Conformance / fuzz トラック（spec-conformance-design.md C4 の継続拡張）

方針 (ユーザー決定 2026-08-07): C# compat の differential fuzz を先に育てる。
hot reload の fuzz は compat 文法の拡張が一巡してから独立に起こす。

- T233 / T234 完 (done.md 参照)。生成文法の現状は FuzzGenerator の
  doc comment が正本
- [ ] **T235** (P2): hot reload fuzz — v1/v2 型定義ペアを生成して reload し、
      不変量 (retained 保持 / added=initializer / identity 維持) を検証。
      differential でなく不変量オラクルの新設計になるため独立タスク

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
