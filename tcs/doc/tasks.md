# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 仕様ベース conformance トラック

設計正本: `doc/spec-conformance-design.md`。C0→C1→C2→C3 直列、C4/C5 は C2 後に並行可。

- **T206** [P1] C2 後半: 既存 TranspileAndRun corpus (326 箇所) の dotnet differential 裏取り (TestHelper 拡張 + opt-out 設計)

C2 spec differential の発見 (baseline に Bug として記録済み、修正で baseline 更新):

- **T194** [P1] メソッド overload / property lowering 名衝突が last-write-wins で silent 誤 dispatch (ParameterArrays3, PropertyReservedSignatures) — TCS1001 (同名 member 衝突)
- **T195** [P1] `new` member hiding が metatable の dynamic dispatch に負ける (VirtualMethods1/2) — TCS1001 (new modifier)
- **T196** [P1] static constructor が silent 欠落、static field 初期化順の意味論差 (StaticConstructors1/2, StaticFieldInitialization2) — cctor は TCS1001、初期化順は個別判断
- **T197** [P1] 式文脈の increment/decrement の副作用が消える・引数評価順 (Run-timeEvalOfArgLists1) — emitter 調査・修正
- **T199** [P1] インスタンス field の default 値が nil になる経路 (VariableInitializers2) — 調査・修正
- **T200** [P1] delegate 宣言 / `new D(...)` が silent 破損 (AnonFuncTypeConv2) — TCS1001
- **T201** [P1] interface の const/field が silent 欠落 (InterfaceFields) — TCS1001
- **T202** [P1] char/hex escape の Lua 変換破損 `'\x9'` → `"\x9G"` (CharacterLiterals) — emitter 修正
- **T203** [P1] Console マッピングが効かない文脈がある (PatternFormGen1/2) — 調査・修正
- **T198** [P2] 補間文字列の alignment / format specifier が無視される (DeclCustomHandler1) — 対応 or 診断
- **T204** [P2] namespace 参照解決の破綻 (ExtensionMethodInvocations2) — namespaced 入力の既知制約と統合判断

- **T185** [P2] C3: baseline 後退検知の恒常ゲート化 (配置は実測で決定)
- **T186** [P2] C4: サブセット内 AST 生成 fuzz + 自動縮小 + nightly
- **T187** [P2] C5: coverlet で emitter 未踏分岐の可視化

---

その他は需要駆動で起票する。

- lub 検証トラックの追加サンプル移植・Useful 層追加は需要駆動
- 診断一致 (analyzer / check / transpiler) とファイルサイズ (600/800 行) は
  run-tests の恒常ゲートで守る
