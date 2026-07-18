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

- [x] T224 完 (2026-07-18): IlExport 契約完備、fallback 構文の整理
      (static method group IL 化 / instance method group・定数式 alignment
      診断化 / lock・using 等は既存診断)。legacy visitor は診断出力と
      挙動不変の保険として恒久保持


### Phase 3 — 価値の刈り取り

- [ ] **T218** (P1, luo 側): M3 IL→C backend。object model は spike 決着
      どおり IL-native。第四マイルストーンまで完了 (継承: 範囲型 ID /
      chain flatten / 推論 dispatch / base 直呼び — done.md 参照)。
      残り: Dict runtime、closure、ctor 連鎖 (base 初期化子の契約化)、
      静的 link 出荷形
- [ ] **T219b** (P2): struct の残り — record struct、struct の
      method/property/ctor、struct 型 field、nested struct field。
      v1 (データ struct) の需要を見て拡張
- [ ] **T220** (P2): migration metadata（il-design.md §6）の実装 —
      仕様は T217 で定義済み。実装は dev ホットリロードの需要と同期して着手

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
