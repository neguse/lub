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
      class 骨格 / field initializer 式の IL 化（program 構造の IL 化、
      M2 で luo 入力契約を決める際に形を確定）もここ
- [ ] **T225** (P2): IIFE の statement 化（examples 決定 2）— IL 上の
      出力改善。M1 の挙動不変契約から切り離して実施

### Phase 2 — 契約の確立（luo が独立実装できる状態を作る）

- [ ] **T226** (P2): double / long のサブセット外化 — il-design §4 の
      診断化 (TCS1001) とテスト/サンプル資産の f32 リテラル移行。
      fuzz の 32bit overflow 回避制約の撤廃 (int32 wrap が .NET と一致した)
      もここで

### Phase 3 — 価値の刈り取り（T218 と T219 は並行可）

- [ ] **T218** (P1, luo 側): M3 IL→C backend。object model は spike 決着
      どおり IL-native。第一マイルストーン完了 (luoc/ 骨格、digest kernel
      3/3 一致 — done.md 参照)。残り: 対応 IL ノードの全域化、
      class 骨格/文字列/List/Dict runtime、静的 link 出荷形
- [ ] **T219** (P1): M5 struct / record struct のサブセット追加 —
      Lua 側表現（table of tables vs userdata 連続バッファ）は T212 の
      particles 実測で決定。TCS1001 解除、値意味論の semantic テスト、
      digest harness へ particles struct 版を追加
- [ ] **T220** (P2): migration metadata（il-design.md §6）の実装 —
      仕様は T217 で定義済み。実装は dev ホットリロードの需要と同期して着手

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
