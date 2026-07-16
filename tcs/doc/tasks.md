# タスクリスト

完了したタスクは `doc/done.md` に移動し、ここから削除する。

優先度:
- **P0**: 正しさ・再現性のブロッカー、または目的直結の検証
- **P1**: 仕様ギャップ・開発体験上の不足
- **P2**: 整備・拡張・ドキュメント同期

---

## 推奨着手順

2026-07-12 の全体コードレビューで、既存テストが通っていても
`tcs check` 後の生成 Lua が C# と異なる結果になる経路を確認した。
タスク番号順ではなく、次の依存順で着手する。

1. **型・メンバー意味論**: T150、T180 (並行可)
2. **runtime 契約**: T152 → T153
3. **CLI / watch**: T155 → T157
4. **保守性・文書同期**: T158 → T159 → T160 → T161

lub Haxe 代替検証は breakout 級サンプルの実機動作まで完了した
(`doc/lub-gap-analysis.md`)。以降のサンプル移植・Useful 層追加は需要駆動で切る。

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: 正しさ・安全性

### T180: 値型/string の型パターンが nil にマッチする問題
- 目的: `v is int inner` が `getmetatable(v) == int` (未定義 global = nil 比較) になり、nil receiver が値型パターンにマッチして束縛される silent wrong-code を直す。`is string` も string metatable と型 table の比較で常に false になる
- 作業:
  - DeclarationPattern / TypePattern / ConstantPattern(型) の対象型が値型・string のとき、metatable 比較ではなく型別判定 (number/boolean/string の `type()` 判定、nullable 透過の receiver は `~= nil`) を emit する
  - 型消去で判定できない組合せは TCS1001 で明示する
  - nil / 非 nil / 型不一致の semantic test を追加する
- 完了条件: `((int?)null) is int` が false、値ありは true になり、bool/string パターンも C# と一致する

### T150: 暗黙 base() と constructor chaining の境界整理
- 目的: initializerなしの派生constructorでC#が暗黙に呼ぶ`base()`を実行し、基底field初期化を保持する
- 依存: なし (T149 完了済み)
- 作業:
  - 合成default constructorと明示constructorの双方で非object基底`Base.new()`を呼ぶ
  - 既存のexplicit `base(args)`を維持し、未対応の`this(args)`/複数constructorはTCS1001で明示する
  - 基底→派生のfield initializer/constructor body順をtestする
- 完了条件: `class B { int X = 42; } class D : B {}`の`new D().X`が42となり、constructor formが黙って別の意味にならない

### T152: empty sequence と型別 default のLINQ/runtime契約
- 目的: `FirstOrDefault<int>`等が常にnilを返し、allowlist済みAPIが値型で実行時エラーになる問題を直す
- 依存: なし (T139 完了済み)
- 作業:
  - element/result typeのdefaultをemitterからruntimeへ渡す共通経路を作る
  - `First`/`Last`のempty error、`FirstOrDefault`/`LastOrDefault`のint=0/bool=false/ref=nilを実装する
  - 非nullable `First`/`Last`/`Min`/`Max`のempty/predicate missは明確なruntime errorへ揃える
- 完了条件: empty sequenceのint=0 / bool=false / reference=nilが一致し、First/Last/Min/Maxのempty/predicate missがnil算術ではなく明示errorになる

### T153: ToDictionary selector の一回評価
- 目的: key/value selector が要素ごとに複数回評価され、副作用付き selector の結果が C# とずれる問題を直す
- 依存: なし (T139 完了済み)
- 前提: duplicate key で throw する C# 契約の再現は棚卸し (2026-07-17) で見送り — もともと不正なプログラムの挙動差であり、正しいプログラムを壊さない。`Dictionary.Add` / `ToDictionary` の duplicate は Lua 上書き挙動を既知差異として扱う
- 作業:
  - key/value selector を各要素 1 回だけ評価し、評価順 (key → value) を C# に揃える
  - 呼出回数・順序を semantic test で固定する
  - duplicate key の上書き挙動を support-matrix の既知差異へ記載する
- 完了条件: 副作用付き selector の呼出回数・順序が C# と一致し、既知差異が文書化されている

---

## P1: CLI・開発体験

### T155: --entry を実際の emitted type/name と一致させる
- 目的: namespaced classやinterfaceをentryに指定したとき、存在しないLua名をreturnして成功扱いする問題を直す
- 依存: なし (T151 完了済み)
- 作業:
  - `INamedTypeSymbol`からemitterの実Lua名を取得し、文字列を直接appendしない
  - class/record classだけを許可し、interface/ref-only/非emit型を拒否する
  - namespace flatten時のsimple/fully-qualified指定と同名型の曖昧性を定義する
- 完了条件: `--entry Game.App`と一意なsimple `App`は正しいtableを返し、同名namespace型/interface/ref-onlyはexit 1かつoutput未作成になる

### T157: watch の --prelude dependency監視
- 目的: outputへ埋め込まれるpreludeだけを変更してもrebuildされない問題を直す
- 作業:
  - prelude pathをinput/refと同じbuild dependency集合へ追加する
  - `*.cs`固定filterをやめ、`.lua` preludeのChanged/Created/Renamed/Deletedをexact path判定で拾う
  - prelude-only atomic saveのwatch testを追加する
- 完了条件: C#に変更がなくてもprelude更新でoutputが再生成され、削除時は失敗を報告し、無関係ファイルではrebuildせず、test後にwatch子processが残らない

---

## P2: 保守性・ドキュメント

### T158: LuaEmitter.Expressions.cs の責務分割
- 目的: 800行禁止を超えたemitterを、変更競合と意味論漏れを起こしにくい単位へ分ける
- 依存: T139-T148、T151-T153
- 作業:
  - operator/assignment、invocation/API mapping、object/collection、pattern/null/lambda等のpartial fileへ分割する
  - mapping tableとfresh-temp/lvalue helperの所有箇所を一意にする
  - behavior/source mapに差分がないことを全testで確認する
- 完了条件: 各C# sourceが600行以下を目安とし、少なくとも800行未満になる

### T159: TinyCsComplianceFacts.cs の責務分割
- 目的: 600行警告域に入った共有factsを、syntax/API/collection-null/formattingへ分ける
- 依存: なし (T133/T137/T138 完了済み)
- 作業:
  - AnalyzerとTranspilerが同じsourceを参照できるproject構成を維持してpartial fileへ分割する
  - allowlistとdiagnostic formattingの重複を作らない
- 完了条件: 各fileが600行以下になり、Analyzer/check parity testとpackage consumer gateが通る

### T160: 600/800行のfile size gateを自動化
- 目的: CLAUDE.mdの600行警告・800行禁止が手動確認だけで再び破られないようにする
- 依存: T158、T159
- 作業:
  - tracked C# sourceを対象に、600行超をwarning、800行超をerrorにする単一のcross-platform checkerを追加する
  - checkerを`run-tests.sh` / `run-tests.ps1` / CIの双方から呼び、判定を二重実装しない
  - generated/bin/obj/depsを除外する
- 完了条件: 601行fixtureはwarning/exit 0、801行fixtureはerror/nonzeroとなり、現行sourceではerror 0になる

### T161: support matrix / test evidence の最終監査
- 目的: 一連の修正後に、実装・semantic test・support表のずれを残さない
- 依存: T133-T160、T162
- 作業:
  - 実test discovery結果を基準にcurrent/READMEの件数を更新する
  - 本レビューで修正したproperty/operator/inheritance/String/LINQ/CLIのsupport matrix記述を再監査する
  - liveな累計件数を残すなら自動consistency gateを追加し、難しければcurrentから件数を削除して`run-tests`を正本にする
  - 各タスクで更新済みのtasks/current/doneを最終確認する（done.mdの歴史的件数は変更しない）
- 完了条件: `dotnet test`と文書の件数・対応状態が一致または自動同期され、既知の「Yだが実行不能」記述が残っていない

---

増分 module compilation track (T172-T179) は完了 (done.md)。設計の正本は
`doc/incremental-module-compilation-design.md`。
