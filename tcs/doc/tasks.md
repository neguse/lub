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
- [ ] **T219b** (P1 へ格上げ): struct の残り (record struct、struct の
      member、struct 型 field)。需要シグナル: perf bench の particles_struct
      が tcs2c 未対応で毎 push "-" 表示 (2026-07-18)。tcs2c 側の struct
      対応も本タスクの範囲
- [ ] **T220** (P1、ゲート解除 2026-07-18): hot reload の実装。ユーザー判断で
      「cold reload 安全弁止まり」を却下し、il-design §6 の CLOS 流 eager
      migration を実装対象とする。検証面は同一 VM 内で 2 版を transpile して
      reload するセマンティックテストで立てる (lub 側導線は待たない)。段階:
      - [x] (a) layout hash の struct 推移展開 (IlExport — struct 内部変更が
            owner class の hash へ伝播。T219b の struct 型 field 解禁に先行)
      - [ ] (b) reload runtime: weak registry + metadata diff 適用
            (added=initializer / discarded=破棄 / retained 保持、in-place で
            identity 維持)、OnReload フック、reload は frame 境界
      - [ ] (c) struct 値の再直列化 migration (owner walk 経由、il-design §6)

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
