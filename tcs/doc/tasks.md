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
   コンパイラのコードを書く前に spike と紙の演習で決着させる
2. **各段階で独立に価値を着地させる** — 途中で止まっても損しない順序
3. **数値モデルの凍結を C backend 着手より前に** — double 前提で
   IL→C を書く手戻りを封じる

### Phase 0 — 情報を買う + 発見バグ修正（並行可）

### Phase 1 — M1: 挙動不変の内部再編 ✓ (T214a-c 完、done.md 参照)

- [ ] **T224** (P2): legacy visitor の縮退 — fallback に残る構文
      （method group 参照、混在 tuple 分解、lock、using 宣言、
      非リテラル alignment、TCS 診断構文）の IL 化 or 診断化を進め、
      fallback を診断 method のみに絞ったうえで legacy 削除を判断。
      class 骨格の IL 化は ctor/accessor/field initializer まで契約化済み
      (2026-07-18)。残りは top-level 文と operator 本文の IlExport 収載
- [ ] **T225** (P2): IIFE の statement 化（examples 決定 2）— IL 上の
      出力改善。M1 の挙動不変契約から切り離して実施

### Phase 2 — 契約の確立（luo が独立実装できる状態を作る）

- [ ] **T219b** (P2): struct の残り — record struct、struct の
      method/property/ctor、struct 型 field、nested struct field。
      v1 (データ struct) の需要を見て拡張
- [ ] **T220** (P2): migration metadata（il-design.md §6）の実装 —
      仕様は T217 で定義済み。実装は dev ホットリロードの需要と同期して着手

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
