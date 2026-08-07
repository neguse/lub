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
- [ ] **T219b** (P1 へ格上げ): struct の残り。需要シグナル: perf bench の
      particles_struct が tcs2c 未対応で毎 push "-" 表示 (2026-07-18)。段階:
      - [x] (a) instance member 解禁 (2026-08-08): method / property /
            単一のパラメータ付き ctor を静的自由関数へ emit。receiver 規則
            (変数=直渡し / rvalue=copy) 込み。static member・operator・
            indexer は引き続き診断
      - [ ] (b) record struct (== / Equals / with を生成静的関数で)
      - [ ] (c) readonly (record) struct の copy 全省略
      - [ ] (d) tcs2c 側の struct member / record struct 対応
      設計方針 (2026-08-07 討議):
      - Lua 表現は v1 の plain table + copy 地点 scopy を不変のまま拡張する。
        metatable は導入しない
      - member (method/property/ctor) は静的自由関数へ emit — struct は
        継承がなく呼び出しサイトの静的型が常に確定するため動的ディスパッチ
        不要。record struct の ==/Equals/with も生成静的関数で賄う
      - `readonly struct` / `readonly record struct` は不変性により alias が
        観測不能なので **scopy を全省略** (性能レバー。Roslyn が不変性を
        コンパイル時保証)
      - scalarization (局所 SROA / SoA 化) は観測等価な最適化 tier として
        分離し、baseline のマッピングには混ぜない (bench 需要駆動)
      - dev backend に C 層 struct (userdata) は持ち込まない — live 移行の
        単純さを優先。逃げ道は struct 非依存の汎用 byte-buffer userdata
        (offset は生成 Lua 側定数、reload で C 再コンパイル不要) で、採否は
        spawn_churn 系 bench の実測が出てから
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
        の migration は需要待ち

---

## Conformance / fuzz トラック（spec-conformance-design.md C4 の継続拡張）

方針 (ユーザー決定 2026-08-07): C# compat の differential fuzz を先に育てる。
hot reload の fuzz は compat 文法の拡張が一巡してから独立に起こす。

- [ ] **T233** (P1): fuzz 文法拡張 第2弾。ユーザー定義オーバーロードは
      サブセット外 (TCS1001 MethodOverload) で対象外。段階:
      - [x] (a) 制御フロー + コレクション: 有界 while / switch 文・式 /
            break・continue / foreach / 三項 / 変数除数 (正数ガード) /
            List indexer・Sort・RemoveAt / Dictionary (両キー型、列挙なし、
            固定キープローブ)
      - [x] (b) class/record 生成 (field / auto property / instance method /
            継承 / 実行時条件の virtual dispatch)、record with 式・値等価、
            is / is-designation / property pattern
- [ ] **T234** (P2): fuzz 文法拡張 第3弾。段階:
      - [x] (a) LINQ 小核 (Where/Select/Sum/Count/Min/Max/Any/All/
            OrDefault 系/OrderBy/Take/Skip、例外安全形のみ) +
            Split/Join/IsNullOrEmpty。Sum は要素 %1000 有界化 (C# の Sum は
            checked で overflow throw)、ToDictionary はキー重複 throw のため
            生成しない
      - [ ] (b) struct copy セマンティクス (T219b 解禁後)
- [ ] **T235** (P2): hot reload fuzz — v1/v2 型定義ペアを生成して reload し、
      不変量 (retained 保持 / added=initializer / identity 維持) を検証。
      differential でなく不変量オラクルの新設計になるため独立タスク

---

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
