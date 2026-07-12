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

1. **即時安全性**: T136
2. **診断契約**: T133 → T137 → T138
3. **Lua 命名基盤**: T151
4. **式 lowering 基盤と評価回数**: T139 → T140 → T141 → T142 → T143 → T144
5. **型・メンバー意味論**: T145 → T146 → T147 → T148、並行して T149 → T150
6. **runtime 契約**: T152 → T153 → T154
7. **CLI / watch**: T155 → T156 → T157
8. **保守性・文書同期**: T158 → T159 → T160 → T161

lub Haxe 代替検証は breakout 級サンプルの実機動作まで完了した
(`doc/lub-gap-analysis.md`)。以降のサンプル移植・Useful 層追加は需要駆動で切る。

前提: `../lub` は readonly。lub 側に変更が必要な場合は feature request を出し、
tcs 側から直接変更しない。

---

## P0: 正しさ・安全性

### T133: 完全修飾 API アクセスの TCS1002 誤検出修正
- 目的: `System.Math.Min(...)` のような完全修飾アクセスで `System.Math` の部分式が TCS1002 (unsupported API) に誤検出されるのを直す
- 作業:
  - TryGetUnsupportedApi の member access 判定で、supported 型そのものを指す qualified name を除外する
  - analyzer / `tcs check` / transpiler warning の共有 facts で同じ判定にする
- 完了条件: `System.Math.Min(1, 2)` が診断なしで通り、`System.IO.File.ReadAllText` は引き続き TCS1002 になる

### T136: Lua 実行テストの timeout と String edge contract 修正
- 目的: supported な `String.Replace` が無限ループする問題を直し、同種のhangでテスト全体が停止しないようにする
- 作業:
  - `TestHelper.RunLua`とversion取得を有限時間の共通process helperへ寄せ、stdout/stderrを非同期drainする
  - timeout時はprocess treeをkillし、drain/Wait後に診断付きで失敗させる
  - `String.Replace(str, "", replacement)` をC#と同様に即時エラーへする
  - `EndsWith("") == true`、`Split("")`は元文字列1要素、`Split()`はwhitespace separatorとなる.NET契約を含め、allowlist済みString APIの空文字edgeを棚卸しする
- 完了条件: 空 `oldValue` のReplaceが短時間で失敗し、EndsWith/Splitの空引数が.NET比較testと一致し、意図的hangテストのtimeout後に子processが残らない

### T137: partial / lock の Analyzer / check / emitter 診断を統一
- 目的: レビューで確認した`partial`上書きと`lock`本体消失を、全診断経路で黙殺しない
- 依存: T133
- 作業:
  - class/record/interface declarationの`partial` modifierと`LockStatement`を共有factsでTCS1001にする
  - Analyzerと`check`の対象ケースtestを追加し、既存の診断一致gateへ乗せる
  - emitter fallbackでもwarningを残し、複数partial宣言やlock bodyを無言で別意味へしない
- 完了条件: `partial class`と`lock`がAnalyzer/checkで同じTCS1001、`tcs check`はexit 1となり、通常transpileも警告なしで黙殺しない

### T138: supported API allowlist を完全シグネチャ単位へ変更
- 目的: method名だけが一致する未実装overloadを許可し、runtimeで落ちる問題をなくす
- 依存: T133、T137
- 作業:
  - `IMethodSymbol.OriginalDefinition`、receiver、static/reduced-extension、generic arity、parameter/ref-kind/delegate arityで許可可否を判定する
  - indexed `Select((x, i) => ...)`、comparer、`StringComparison`、明示default、未実装List/Dict constructor overloadをTCS1002にする
  - 実装済みoverloadはAnalyzer / check / emitterで同じく許可されることをtest matrixで固定する
- 完了条件: 各代表negativeがAnalyzer/checkの両方で同じTCS1002となってcheck exit 1、通常のSelect/Where等は誤検出なしで通る

### T139: 副作用を一度だけ評価する expression lowering 基盤
- 目的: 式文字列の複製によるreceiver/operandの多重評価を、後続構文でも再利用できる形で止める
- 依存: T151
- 作業:
  - setup statements、value、typeを持つ小さなlowering結果とfresh temp生成器を導入する
  - Roslyn `IOperation` / symbol情報を利用し、`?.` receiverをIIFE内localへ一度だけ保存する
  - generated temp名がユーザーsymbolと衝突しないことを保証する
- 完了条件: 副作用付き`Next()?.Value` / `Next()?.Method(Arg())` / `Next()?[Index()]`でreceiverが1回だけ呼ばれ、null時は引数/indexを評価せず、non-null時もC#と一致する

### T140: switch governing expression の一回評価
- 目的: switch statement/expressionの対象式をcase/armごとに再実行しない
- 依存: T139
- 作業:
  - governing expressionをswitch開始時のlocalへ保存し、全case/pattern/whenで再利用する
  - statementとexpression、default、property/relational patternを同じ方針へ揃える
- 完了条件: counter付き対象式がswitch全体で1回だけ実行され、case数やdefault位置に依存しない

### T141: deconstruction RHS の一回評価
- 目的: `var (a, b) = Make()`でRHSを要素数分呼び出す問題を直す
- 依存: T139
- 作業:
  - RHSをlocalへ一度保存してからproperty/deconstruct値を展開する
  - declaration/assignment deconstructionとdiscardを同じ経路で扱う
- 完了条件: 副作用付きRHSが1回だけ評価され、全要素が同じinstance/valueから取得される

### T142: with expression receiver の一回評価
- 目的: copy元の式をtable走査とmetatable取得で複数回評価しない
- 依存: T139
- 作業:
  - receiverをlocalへ保存し、shallow copyとmetatable取得の双方で再利用する
  - override式のC#評価順をtestで固定する
- 完了条件: 副作用付きreceiverが1回だけ実行され、copy元identityとoverride順がC#と一致する

### T143: assignment/lvalue の一回評価とcompound semantics
- 目的: indexer/property/receiverをcompound assignment、`??=`、increment、collection mutationで重複評価しない
- 依存: T139
- 作業:
  - read/writeを分離したlvalue loweringを実装し、receiverとindexをtempへ保存する
  - `+=`のstring concatを含め、symbol/typeに応じたcompound operatorへ変換する
  - `List.Clear`等のreceiver埋め込みも一回評価へ揃える
- 完了条件: 副作用付き`Get()[NextIndex()] += value`、`??=`、increment、`GetList().Clear()`がreceiver/index/RHSのC#評価回数・順序に一致する

### T144: simple for 最適化の動的条件セマンティクス修正
- 目的: C#では各iterationで再評価される終端条件を、Lua numeric forが一度だけ評価する差をなくす
- 依存: T139
- 作業:
  - numeric forへ落とせるloop-invariant/pureな条件を限定する
  - method call、可変field/property、loop内で変更されるbound/loop variableを含む場合はwhile loweringへfallbackする
  - continue時もincrementorとconditionの順序を保持する
- 完了条件: 副作用付き/mutable boundのforが各iterationで条件を再評価し、単純な`i < n; i++`は既存最適化を維持する

### T145: C# の除算・剰余セマンティクス
- 目的: Luaの`/`とfloor由来`%`をそのまま使うことで、整数・負数の結果がC#とずれる問題を直す
- 依存: T139、T143
- 作業:
  - operand/result typeから整数除算、浮動小数除算、C# remainderを判定する
  - 0方向truncationと`a - trunc(a / b) * b`相当をruntime helperまたは安全なloweringで実装する
  - `/=` / `%=`、正負の組合せ、整数0除算errorと浮動小数のIEEE結果を型別に扱う
- 完了条件: `5 / 2 == 2`、`-5 / 2 == -2`、`-5 % 2 == -1`となり、整数/浮動小数の0除算とcompound casesもC#期待値に一致する

### T146: nullable bool と GetValueOrDefault のnil-safe lowering
- 目的: Luaの`or`が`false`もfallback扱いするため、nullable boolの値を壊す問題を直す
- 依存: T139、T143
- 作業:
  - `??`を明示的な`nil`判定へ変更し、左辺を一回だけ評価する
  - `GetValueOrDefault()`と`GetValueOrDefault(fallback)`をoverload別に実装し、receiver→fallback引数の順で常に各1回評価する
  - `??=`はT143のlvalue loweringを利用する
- 完了条件: `bool? false ?? true`がfalse、nullがtrueとなり、`??`右辺はnull時だけ、GetValueOrDefaultの明示fallback引数は値の有無によらず1回評価される

### T147: custom property accessor のread/write lowering
- 目的: 生成済み`get_`/`set_`を呼ばずraw fieldとして読み書きする状態を直す
- 依存: T139、T143
- 作業:
  - `IPropertySymbol`とsyntaxからauto/custom propertyを判別する
  - instance/implicit-thisのread/writeを`get_`/`set_` callへ変換する
  - compound assignment、object initializer、conditional access、property pattern、get-only/set-onlyを同じproperty loweringで扱う
- 完了条件: getter/setterに副作用や変換があるpropertyがC#と同じ値・呼出回数になり、auto propertyのraw field表現は維持される

### T148: static property のstorage/accessor lowering
- 目的: static auto propertyをinstanceの`self`へ初期化し、static accessがnilになる問題を直す
- 依存: T147
- 作業:
  - static auto propertyをclass tableへ型別default/initializer付きで出力する
  - static custom getter/setterをclass functionとして生成・呼出する
  - instance生成の有無に関係なくstatic値が共有されることをtestする
- 完了条件: initializer/default/read/write/custom accessorのstatic property casesがC#と一致する

### T149: 継承リンクをソース順・ファイル順から独立化
- 目的: 派生classが基底classより先にemitされるとmetatable linkが失われる問題を直す
- 依存: T151
- 作業:
  - class table宣言、継承link、constructor/member定義を複数passへ分けるか、型依存graphで順序付ける
  - 同一ファイル逆順、複数ファイル逆順、複数段継承をtestする
- 完了条件: 入力ファイルと宣言順を入れ替えても継承method/property lookupが同じ結果になる

### T150: 暗黙 base() と constructor chaining の境界整理
- 目的: initializerなしの派生constructorでC#が暗黙に呼ぶ`base()`を実行し、基底field初期化を保持する
- 依存: T149
- 作業:
  - 合成default constructorと明示constructorの双方で非object基底`Base.new()`を呼ぶ
  - 既存のexplicit `base(args)`を維持し、未対応の`this(args)`/複数constructorはTCS1001で明示する
  - 基底→派生のfield initializer/constructor body順をtestする
- 完了条件: `class B { int X = 42; } class D : B {}`の`new D().X`が42となり、constructor formが黙って別の意味にならない

### T151: C# symbol から安全な Lua 名への中央マングリング
- 目的: C#では合法な`repeat`等の名前や`@identifier`が、生成Luaをsyntax errorにする問題をなくす
- 作業:
  - Lua予約語、escaped identifier、generated temp/labelとの衝突を扱うsymbol-based命名器を追加する
  - type/method/local/parameter/field/propertyと`--ref` host memberのdot/bracket accessを一貫して変換する
  - call site、entry、hot reloadで同じemitted nameを参照できるAPIにする
- 完了条件: `repeat`、`@end`、`self`、generated tempと衝突する名前を含むC#がvalid Luaとなり、host wire名も保持される

### T152: empty sequence と型別 default のLINQ/runtime契約
- 目的: `FirstOrDefault<int>`等が常にnilを返し、allowlist済みAPIが値型で実行時エラーになる問題を直す
- 依存: T138、T139
- 作業:
  - element/result typeのdefaultをemitterからruntimeへ渡す共通経路を作る
  - `First`/`Last`のempty error、`FirstOrDefault`/`LastOrDefault`のint=0/bool=false/ref=nilを実装する
  - 非nullable `First`/`Last`/`Min`/`Max`のempty/predicate missは明確なruntime errorへ揃える
- 完了条件: empty sequenceのint=0 / bool=false / reference=nilが一致し、First/Last/Min/Maxのempty/predicate missがnil算術ではなく明示errorになる

### T153: Dictionary Add / ToDictionary のduplicate key契約
- 目的: C#では例外になるduplicate keyをLua table上書きとして黙って通さない
- 作業:
  - instance `Dictionary.Add`をraw代入ではなく`Dict.Add` helperへrouteし、既存keyで明確なLua errorを返す
  - `ToDictionary`のkey/value selectorを各要素1回だけ評価し、duplicate時に既存値を上書きしない
  - collection initializerのAdd形式はduplicate error、index initializer/indexer assignmentは上書き可能なまま区別する
  - string/number keyとselector重複をsemantic testへ追加する
- 完了条件: Add/ToDictionaryのduplicateは失敗し、indexer updateは成功する

### T154: HotReload.swap のtransactional rollback
- 目的: reload中に既存tableを変更してからerrorになると、失敗後にも変更が残る問題を直す
- 作業:
  - staging `_G`とmutable table graphをlive側と共有しない独立環境で成功時だけcommitするか、失敗時にin-place復元できる循環/metatable対応snapshot方式を採用する
  - 既存table mutation、新規global、metatable変更後のerrorをrollback testに追加する
  - 成功時のtable identityとinstance state保持は維持する
- 完了条件: failed swap後のLua global object graph（binding/nested table/metatable/cycle）が旧状態へ戻り、successful swapの既存identity保持testも通る。I/O等の外部副作用はrollback対象外と明記する

---

## P1: CLI・開発体験

### T155: --entry を実際の emitted type/name と一致させる
- 目的: namespaced classやinterfaceをentryに指定したとき、存在しないLua名をreturnして成功扱いする問題を直す
- 依存: T151
- 作業:
  - `INamedTypeSymbol`からemitterの実Lua名を取得し、文字列を直接appendしない
  - class/record classだけを許可し、interface/ref-only/非emit型を拒否する
  - namespace flatten時のsimple/fully-qualified指定と同名型の曖昧性を定義する
- 完了条件: `--entry Game.App`と一意なsimple `App`は正しいtableを返し、同名namespace型/interface/ref-onlyはexit 1かつoutput未作成になる

### T156: stdout sourcemap にruntime/prelude offsetを反映
- 目的: `-o`なしの`--sourcemap`だけ行番号offsetが欠落する問題を直す
- 作業:
  - stdout分岐でも`ToJson(sourceMapLineOffset)`を使用する
  - runtime有無、user prelude有無、両方ありの実Lua行とmapping keyをtestする
- 完了条件: stdoutへ出したLuaのmapped statement行とstderr JSONのkeyが全構成で一致する

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
- 目的: 1,111行で800行禁止を超えたemitterを、変更競合と意味論漏れを起こしにくい単位へ分ける
- 依存: T139-T148、T151-T153
- 作業:
  - operator/assignment、invocation/API mapping、object/collection、pattern/null/lambda等のpartial fileへ分割する
  - mapping tableとfresh-temp/lvalue helperの所有箇所を一意にする
  - behavior/source mapに差分がないことを全testで確認する
- 完了条件: 各C# sourceが600行以下を目安とし、少なくとも800行未満になる

### T159: TinyCsComplianceFacts.cs の責務分割
- 目的: 624行で警告域に入った共有factsを、syntax/API/collection-null/formattingへ分ける
- 依存: T133、T137、T138
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
- 依存: T133-T160
- 作業:
  - 実test discovery結果を基準にcurrent/READMEの件数を更新する
  - 本レビューで修正したproperty/operator/inheritance/String/LINQ/CLIのsupport matrix記述を再監査する
  - liveな累計件数を残すなら自動consistency gateを追加し、難しければcurrentから件数を削除して`run-tests`を正本にする
  - 各タスクで更新済みのtasks/current/doneを最終確認する（done.mdの歴史的件数は変更しない）
- 完了条件: `dotnet test`と文書の件数・対応状態が一致または自動同期され、既知の「Yだが実行不能」記述が残っていない
