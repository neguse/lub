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

- [ ] **T221** (P0, バグ): closure が捕捉する `for` 変数の意味論 —
      C# は全反復で 1 変数（実測 `3 3 3`）、現行出力は numeric for のため
      反復ごと（実測 `0 1 2`）。修正は while + block への脱糖（M1 の
      lowering 決定と同型）。M1 に先行して直すか M1 に畳むかは着手時に判断
- [ ] **T222** (P0, バグ): `is` / switch 型判定が継承で偽 —
      `getmetatable(x) == T` の完全一致比較のため、派生インスタンスへの
      `is Base` が C# `true` / 現行出力 `false`（実測）。metatable chain を
      辿る runtime helper へ置換。M1 と独立に修正可能
- [ ] **T223** (P1): interface を対象とする type test / cast の診断化 —
      interface は実行時表現を持たないため `x is I` は偽陰性になる
      （il-spec §2 で診断化を決定）。孤立 surrogate を含む string literal の
      診断化（il-spec §11）も同じ Shared facts 追加で扱う
- [ ] **T212** (P0, luo 側): 性能上界 spike の PC 測定 —
      `../luo/docs/spike-ceiling.md` の kernels（sprite_update / spawn_churn /
      particles）を手書き C で実測し、aot-slot vs native 比を先に取る。
      実機測定は Playdate SDK / toolchain 導入後に追補
- [ ] **T213** (P1): LUA_32BITS ビルド整備（tcs 側）— CMake で 32bit 変種を
      選択ビルド可能に。spike（T212）と M4（T216）の共有基盤

### Phase 1 — M1: 挙動不変の内部再編

- [ ] **T214** (P0): Transpiler を syntax 走査 → IL → Lua に再編。
      T210 の抽象度決定（`doc/il-lowering-examples.md`）に従い、
      IL ノード定義 + syntax→IL builder +
      IL→Lua emitter へ LuaEmitter 群を段階移行（部分移行中も green を保つ
      ストラングラー方式。Statements → Expressions → Invocations → Patterns 順）。
      完了条件: 全テスト green、増分コンパイル session
      （`doc/incremental-module-compilation-design.md`）と両立、
      fuzz / differential / conformance ゲート不変

### Phase 2 — 契約の確立（luo が独立実装できる状態を作る）

- [ ] **T216** (P1): M4 数値モデル移行 — i32/f32、LUA_32BITS 実行へ切替。
      differential の .NET 側も int/float 比較へ。全ゲートが 32bit ビルドで green。
      **T218（IL→C）の digest baseline より前に完了させる**
- [ ] **T215** (P1): digest harness — spike と同じ 3 kernel を TinyC# で記述し
      （particles は struct 前提のため M5 までは class 版）、f32 量子化値の
      FNV digest を run-tests ゲート化。2 backend 一致テストの正本になる。
      baseline は 32bit ビルド（T216 後）で記録
- [ ] **T217** (P1): M2 IL 仕様の文書化 + metadata 出力。
      合格基準: luo 側が LuaEmitter を読まずに IL 文書だけで実装着手できる

### Phase 3 — 価値の刈り取り（T218 と T219 は並行可）

- [ ] **T218** (P1, luo 側): M3 IL→C backend。object model は T212 の
      合否解釈に従う。完了条件: digest harness で 2 backend 一致
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
