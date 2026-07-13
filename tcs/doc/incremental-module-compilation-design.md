# browser-wasm 向け増分 source module コンパイル設計

> Status: 提案（増分/module path は未実装。T164 の one-shot `WasmCompiler` は実装済み）
>
> Date: 2026-07-14
>
> Scope: `tcs` core / `WasmCompiler` / Lua module runtime。`../lub` は読み取り専用であり、playground 側の変更は consumer integration の feature request として扱う。

## 1. 結論

browser-wasm の対話コンパイルは、毎回プロジェクト全体を作り直す API から、常駐する Roslyn session と source file 単位の Lua artifact へ移行する。

- semantic context は project 全体で共有する。
- 生成単位は **1 source file = 1 source module** とする。1 file に複数の top-level type があってよい。
- 固定 implementation source は build 時に Lua artifact 化し、browser では再生成しない。
- `--ref` host declaration と metadata reference は型検査専用として session 初期化時に一度だけ構築する。
- 通常の method body 編集では、変更した `SyntaxTree` だけを置換・診断・emit する。
- Lua 側は module descriptor を revision 単位で atomic apply し、class table の identity を維持する。
- full snapshot への link は managed 側(`LinkSnapshot`)が行い、host は linked 文字列を保持するだけにする。
- runtime commit は revision 付き ACK で host に通知され、UI 状態と SLO 計測はこの ACK を終点にする。
- cold start は prebuilt Lua で即座に実行し、Roslyn WASM は background で warm-up する。

主 SLO は、現行 playground の 300 ms debounce を含む compile 時間ではなく、ユーザーが知覚する次の区間で定義する。

> **warm method-body edit の edit-stop から Lua patch commit まで p95 < 500 ms**

このため、managed compiler 部分の engineering budget を 275 ms、browser debounce を 75 ms、link・転送・Lua apply を 75 ms とする。50 ms を GC、frame safe point、計測揺れ向けに確保し、最終判定は区間 p95 の足し算ではなく end-to-end p95 の実測で行う。

## 2. 背景と実測

現行 [`Transpiler.TranspileWithDiagnostics`](../Transpiler/Transpiler.cs) は compile ごとに次を行う。

1. editable source、固定 implementation source、`--ref` source をすべて parse
2. `CSharpCompilation.Create`
3. project 全体の `Compilation.GetDiagnostics()`
4. 各 editable tree に naming と 3 系統の TinyC# compliance analysis
5. 全 implementation tree の Lua emit
6. runtime prelude 込みの巨大な Lua string を JSON で返却

2026-07-14 に `../lub` playground の C# 経路を Release / headless Chrome で測った参考値は次のとおり。少数回の観測値であり、正式な benchmark baseline は §15 の harness で採り直す。

| workload | 観測値 | 意味 |
|---|---:|---|
| 現行 full input cold | 約 11.38 s | .NET runtime 起動、Roslyn、固定 library 全生成を含む |
| 現行 full input warm | 約 5.6–7.0 s、p50 約 6.91 s | 約 153 KB / 20 files の固定 `cs-lib` を毎回解析・生成 |
| editable sample + 現行 `--ref` stub のみ | 462–527 ms、p50 487 ms | 固定 implementation emit を除いても 500 ms に余裕がない |
| native full input p50 | 432 ms | `GetDiagnostics` 約 38%、Lua emit 約 32%、compliance 約 25% |

再現可能な baseline は `bench/chrome-compile.mjs`(T173)で採る。2026-07-14 の実測
(headless Chromium + swiftshader、dev server + Release publish の WasmCompiler bundle、
01_triangle、warm-up 5 + n=30): cold(page load → running)11.3 s、
warm edit→compiled p50 5.58 s / p95 6.23 s。上表の少数回観測と整合する。
なお現行 protocol に commit ACK がないため、この baseline の終点は compile 完了で
あって Lua commit ではない(§13.1 導入後に終点を ACK へ切り替える)。

Chrome CPU sample の約 61–66% は Mono WASM interpreter loop の self time だった。したがって AOT は後段の候補にはなるが、全 tree の診断と emit を繰り返す構造を残したままでは主解決にならない。AOT の比較値はまだ取得していない。

[`browser-wasm feasibility spike`](wasm-playground-spike.md) の warm 173 ms は、小入力を Node の wasmconsole で測った 2026-07-12 の GO 判定用データである。今回の Chrome + full `cs-lib` workload とは母集団が異なるため、現行 playground の性能 baseline には使わない。

## 3. 目標と非目標

### 3.1 目標

- warm method-body edit の edit-stop → successful patch commit を p95 500 ms 未満にする。
- 固定 library の規模に比例して毎 edit が遅くならないようにする。
- project 全体の Roslyn 型情報を保ちながら、Lua は source module ごとに生成・cache・更新できるようにする。
- compile error 中も last-good Lua を動かし続け、修正後に最新 revision だけを反映する。
- 既存 instance の table/metatable identity と instance field state を method 更新後も維持する。
- CLI、`TranspileWithDiagnostics`、`--entry`、単一 Lua 出力を compatibility linker 経由で維持する。
- incremental path と authoritative full build の診断結果を一致させる。

### 3.2 非目標

- cold Roslyn compiler ready を 500 ms 未満にすること。初期表示は prebuilt Lua で compiler 起動から切り離す。
- Haxe compiler path の最適化。
- MSBuild solution / 複数 assembly を扱う汎用 incremental build system。
- Roslyn object graph を browser reload 間で永続化すること。
- `../lub` をこの repository から直接変更すること。
- AOT を先に導入して目標達成とみなすこと。
- partial type の module 間合成。partial class / record / interface は現行どおり TCS1001 とする。

## 4. 用語

`module` の意味が複数あるため、本書では次の名前に固定する。

| 用語 | 定義 |
|---|---|
| source module | 正規化した project-relative C# path を ID とする生成・cache・更新単位 |
| runtime type | class / record / enum の安定した Lua table。source module とは 1:1 ではない |
| entry module | lub などの host が `require` / hotswap する最終 Lua module |
| module artifact | 1 source module の manifest、Lua descriptor chunk、source map をまとめた生成物 |
| fixed implementation | 型検査にも Lua 実行にも必要だが、playground edit では変更しない C# source。例: `cs-lib` |
| reference source | `--ref` 相当の host API declaration。型検査専用で Lua を生成しない |
| metadata reference | `System.Runtime.dll`、`TinySystem.dll` など Roslyn の assembly reference |
| semantic surface | 他 source module の binding を変え得る宣言情報の決定的 fingerprint |

source module ID は `/` 区切り、先頭 `./` なし、`.` / `..` なしの相対 path とする。case は保持するが、portable build では case-insensitive collision を error にする。type 名を module ID に使わない。

## 5. 入力レイヤの分離

browser session は入力を四層に分ける。

| 層 | parse | semantic compilation | Lua emit | edit 時の扱い |
|---|---:|---:|---:|---|
| metadata reference | — | yes | no | ABI 変更時だけ session 再構築 |
| reference source (`--ref`) | init 時 1 回 | yes | no | 内容変更時は session 再構築 |
| fixed implementation source | init 時 1 回 | yes | build 時のみ | browser では prebuilt artifact を load |
| editable source module | open/update 時 | yes | affected module のみ | `SyntaxTree` を差し替える |

fixed implementation source と reference source は editable source に依存してはならない。init 時に metadata + reference + fixed だけの base compilation を検証し、editable symbol がなくても解決できることを保証する。この一方向性により、user edit で固定/ref tree を再診断・再生成する必要がなくなる。固定 source hash、reference source hash、metadata image hash、compiler ABI、options から `referenceAbiHash` を作り、prebuilt artifact と session の一致を検証する。

ただし同一 compilation に editable source を追加すると、新しい extension/overload/import candidate が fixed tree の既存 expression binding を変える可能性がある。init 時に fixed tree 内の operation/type reference が選んだ symbol ID と diagnostics から `fixedBindingFingerprint` を作る。surface/file-set change の slow path で再計算し、base 値と異なれば editable change を fatal diagnostic で拒否する。fixed Lua を user declaration によって暗黙に再解釈させない。

`--ref` type と TinySystem / host global は runtime registry の所有対象にしない。editable module が同じ runtime type/global を宣言した場合は ownership collision として失敗させる。

全体のデータフローは次になる。

```text
build time
  fixed implementation ── tcs module compile ──> fixed artifact bundle
  default sample       ── tcs module compile ──> prebuilt entry Lua

browser warm path
  source delta
      │
      v
  IncrementalCompilationSession
    ├─ cached metadata / --ref / fixed SyntaxTree
    └─ current editable CSharpCompilation
      │
      v
  affected ModuleArtifact batch
      │
      ├─ compatibility linker ──> single entry module ──> lume.hotswap
      └─ direct apply payload  ──> ModuleRegistry.applyBatch
                                      │
                                      v
                              stable runtime type tables
```

## 6. source module 境界

v1 の境界は 1 C# source file とする。

- 1 file は複数の class / record / enum / interface を宣言できる。
- interface は semantic surface に含むが Lua definition を持たない。
- namespace は module 境界ではない。
- module ID と runtime type ID を分離する。
- runtime type ID は Roslyn の fully-qualified symbol identity から決定的に作る。
- runtime の emitted name / `_G` alias は T151 の symbol-based naming API を使い、module path や raw simple name から推測しない。
- type を file 間で移動しても、同一 update batch 内で runtime type ID が同じなら ownership を移譲し、table identity を維持できる。

1 type = 1 module にしない理由は、既存 source と固定 library に複数 type/file があり、ファイル分割を性能機構のために強制したくないためである。partial が未対応なので、1 runtime type の定義が複数 source module にまたがる曖昧さはない。

## 7. IncrementalCompilationSession

新しい stateful core API `IncrementalCompilationSession` を導入する。名称は実装時に変更してよいが、責務は次で固定する。

### 7.1 保持する状態

- metadata references
- parse / compilation options (`concurrentBuild: false` を含む)
- reference source と fixed implementation の `SourceText` / `SyntaxTree`
- editable source ごとの現在の `SourceText` / `SyntaxTree` / content hash
- 最新の immutable `CSharpCompilation`
- module ごとの last-good semantic surface、artifact、diagnostics
- runtime type ownership と module dependency の stable ID graph
- base fixed-binding fingerprint
- compiler/reference ABI hash

`SemanticModel` と `ISymbol` は compilation 世代をまたいで cache しない。Roslyn object の identity/hash を永続 ID にせず、path と決定的 symbol string のみを graph に保存する。更新後は古い compilation history を参照から外し、WASM heap が edit 回数に比例して増えないようにする。

### 7.2 初期化

1. metadata reference image を一度だけ `MetadataReference` にする。
2. reference source と fixed implementation source を一度だけ parse する。
3. reference/fixed inputs を診断し、base `CSharpCompilation` と fixed-binding fingerprint を作る。
4. prebuilt fixed artifact manifest の `referenceAbiHash` を照合する。
5. editable source を追加し、initial project compilation を作る。
6. prebuilt editable artifact があれば content/surface hash を照合して last-good として seed し、なければ全 editable module を初回 emit する。

### 7.3 更新

update は複数 file の add/change/delete/rename を 1 revision の batch として受ける。

1. `SourceText.WithChanges` と `oldTree.WithChangedText` を使う。full text しか来ない場合も旧/new text から change range を得る。
2. `CSharpCompilation.ReplaceSyntaxTree` / `AddSyntaxTrees` / `RemoveSyntaxTrees` で新しい analysis head を作る。
3. syntax diagnostics と semantic surface を計算する。
4. §8 の規則で affected module set を決める。
5. affected tree だけ semantic/compliance/naming diagnostics を更新する。
6. project diagnostics cache に error がなければ affected module を emit する。
7. module artifact batch と timing breakdown を返す。

compile error でも analysis head とその file の diagnostics は新 source へ進める。一方、last-good surface/artifact と実行中 Lua は更新しない。次に成功した revision は、直前の壊れた source ではなく last-good artifact の surface と比較して invalidation を決める。

このとき emit 対象の dirty set は「その revision の change list」ではなく **dirty closure** で決める: 現在の contentHash が last-good artifact の sourceHash と一致しない全 editable module を含める。error 中に別 file を編集し、その後 error file だけを直して復帰した場合、change list に載らない file の artifact が stale のまま publish される事故を防ぐ。

## 8. semantic surface と invalidation

「public API hash」では不足する。同一 C# assembly では `internal` member、extension method、operator、const value なども別 file の binding を変え得るため、本設計では semantic surface と呼ぶ。

surface の canonical representation には少なくとも次を含める。

- type kind、fully-qualified name、arity、accessibility
- base type、interface、type parameter constraint
- member kind/name、accessibility、static/instance
- field / property の型
- parameter/return type、**parameter 名**(named argument が他 file の呼び出しを変えるため)、ref kind、optional default とその値
- property accessor shape、operator/conversion signature
- declaration の全 modifier と attribute type/constant argument
- const value、enum member/value
- extension method と import/binding に影響する declaration
- global using、nullable/parse context、top-level statement 有無など compilation-wide context

`ISymbol.GetHashCode()` は使わない。並び順を正規化した deterministic text を hash する。

### 8.1 v1 invalidation rule

| change | diagnostics / emit |
|---|---|
| method/constructor/accessor body のみで surface 不変 | changed source module だけ |
| 変更 span が body に完全包含されない edit(surface hash が不変でも) | full compilation diagnostics + 全 editable source module(無条件 slow path) |
| surface hash の計算に失敗した edit | 同上(fail-safe) |
| private implementation detail だが conservative surface が変化 | full compilation diagnostics + 全 editable source module |
| type/member signature、extension、operator、const、enum surface 変更 | full compilation diagnostics + 全 editable source module |
| file add/delete/rename、type ownership 移動 | full compilation diagnostics + 全 editable source module、1 atomic batch |
| global using、top-level mode、parse/compilation option 変更 | project session 再構築または full editable rebuild |
| reference source / metadata reference / fixed implementation 変更 | session と fixed bundle を再構築 |

surface 変更時に既存 reverse dependency だけを辿る設計は v1 では採用しない。新しい overload や extension method の追加は、以前 edge がなかった source の候補集合と binding を変え得るからである。まず common path である body edit を確実に 500 ms 未満にし、surface change は正しさ優先で全 editable module を再診断・再生成する。

surface/file-set slow path では fixed-binding fingerprint も照合する。editable declaration により fixed implementation の selected symbol または diagnostic が変わる場合は、fixed artifact を再生成せずその edit を拒否する。attribute は emitter が直接使わなくても `[Obsolete(..., true)]` など他 tree の diagnostics/binding を変え得るため、surface から除外しない。

後段で surface change も狭くする場合は、選択済み symbol edge だけでなく namespace import、overload candidate、extension candidate、operator lookup を graph に記録する。単純な reverse type reference graph だけで最適化してはならない。

## 9. 増分 diagnostics

body-only fast path では次だけを実行する。

- changed tree の syntax diagnostics
- changed tree の `SemanticModel.GetDiagnostics()`
- 現行 `CompilationDiagnosticPolicy` による許容診断判定
- changed tree の `NamingAnalyzer`
- changed tree の `TinyCsComplianceFacts` 各 analysis

reference/fixed tree の diagnostics は、editable に依存しないことを init 時に検証したうえで cache する。body-only edit では compilation-wide context が不変なので、`Location.None` などの project diagnostics bucket も前 revision から引き継ぐ。

surface change、file set change、global context changeでは slow path として full `Compilation.GetDiagnostics()` を実行し、tree ごとの bucket と project bucket を再構築する。明示的な CLI build、CI、`Build` API も同じ authoritative full path を使う。syntax/model/full API 間で同じ diagnostic が返る場合は `(id, severity, path, span, message)` を canonical key として deduplicate する。

incremental diagnostics のために compliance rule を別実装してはならない。Analyzer / `tcs check` / transpiler と同じ `Shared/TinyCsComplianceFacts.cs` を呼ぶ。full build と incremental result の differential test を持ち、不一致する diagnostic class が見つかった場合は affected set を広げる。

project のいずれかに fatal error が残る間は artifact batch を publish しない。古い module は動き続ける。warning は module/path/revision とともに返し、同一 warning の UI 重複表示を避けられる ID を持たせる。

## 10. ModuleArtifact

compile/apply の単位は `ModuleArtifactBatch` とする。deleted file には通常 artifact が存在しないため、削除と ownership 移譲を batch metadata で表す。

```text
ModuleArtifactBatch
  projectRevision
  artifacts[]
  removedModuleIds[]
  ownershipTransfers[]
    typeId
    fromModuleId
    toModuleId
  requiresRestart
  restartReasons[]
```

runtime type を所有する module の削除は、同一 batch で同じ type ID/shape の ownership transfer が完了する場合を除き、conservative に `requiresRestart` とする。interface-only module など runtime ownership がない tombstone は live apply できる。file rename/type move は remove/add/transfer を必ず同一 batch に入れ、中間的な ownership collision や type 消失を見せない。

各 artifact は最低限次の情報を持つ。

```text
ModuleArtifact
  moduleId
  projectRevision
  sourceHash
  semanticSurfaceHash
  compilerAbi
  referenceAbiHash
  dependencyTypeIds[]
  ownedTypes[]
    typeId
    kind
    ownedDefinitionKeys[]
    staticInitializerDependencyTypeIds[]   -- type 単位。initializer 式から到達する
                                           -- 推移的 call closure まで含める
    ownedStaticFields[]
      key
      initializerHash
      hotInitKind (constant | default | restart)
    instanceShapeHash
  luaDescriptorChunk
  chunkName
  sourceMap
```

static initializer の依存は **type 単位**で持つ。module 単位では、同一 file の型 A,C と
別 file の型 B に A → B → C の初期化順が要る場合に interleave できない。依存の抽出は
initializer 式直下だけでなく、そこから到達する project 内 method の推移的 call closure を
memo 付きで辿る。仮想呼び出し等で確定できない type は order-unresolved とし、
typeId sort で決定的に実行する(pre-zero により未初期化読みは nil ではなく型 default になる)。

artifact cache key は source hash だけにしない。最低でも compiler ABI、reference ABI、options、module ID、source hash、binding に使った dependency surface fingerprint を含める。

Lua chunk は評価時に `_G` や live type table を変更せず、descriptor を返す。

```lua
return {
  id = "game/Player.cs",
  revision = 42,
  declare = function(registry) ... end,
  define = function(registry) ... end,
  initializers = {
    ["game/Player.cs#Player"] = function(registry) ... end,
    ["game/Player.cs#PlayerConfig"] = function(registry) ... end,
  },
}
```

static initializer と top-level statement は descriptor 評価時には実行せず、**type 単位**の明示的な initializer thunk に分離する。これにより全 chunk の load/ABI/ownership 検証を live graph の変更前に完了でき、cross-module の type 依存順にも interleave できる。一般の `dependencyTypeIds` と per-type の `staticInitializerDependencyTypeIds` は分け、method body の参照で初期化順が変わらないようにする。

## 11. Lua ModuleRegistry と atomic apply

browser/watch の主経路は、現行 `HotReload.swap` の全 `_G` recursive merge ではなく `ModuleRegistry.applyBatch(batch)` を使う。

descriptor chunk は module 専用の read-only `_ENV` で load する。名前解決は「registry の emitted type alias → TinySystem/runtime global → host global」の順とし、未宣言 global への write は error にする。生成 method closure もこの環境を capture するため、`Foo.new`、`Base.Method`、`Math.*` など現行 emitter の global read は stable registry table へ解決される。実 `_G` alias は legacy linked output の互換層に限定する。

### 11.1 三段階 apply

1. **declare**: batch 内の runtime type table をすべて確保する。既存 type は同じ table を再利用する。あわせて **pre-zero** を行う: fresh VM では全 owned static field、hot apply では新規 field だけを、型別 default(int/float → 0、bool → false、参照型 → nil)で初期化する。initializer 実行前に static を読んでも C# の「未初期化 static は default」契約を破って nil にならない。
2. **define**: inheritance link、constructor、method、accessor、metamethod、enum memberを設定する。
3. **initialize**: fresh VM の initial load では全 type の initializer thunk、hot apply では新規 static field の副作用なし initializer だけを実行する。

全 type を先に declare するため、source/file 順に依存せず cross-module 継承を link できる。fresh VM での initialize 順は per-type `staticInitializerDependencyTypeIds` の topological order、cycle / order-unresolved 内は typeId sort として決定的にする。これは現行の入力列挙順依存を置き換える TinyC# の eager-initialization 規則であり、C# CLR の lazy type initializer と完全同一とはしない(cycle 内では pre-zero した default が観測され得る)。

### 11.2 ownership-aware shallow patch

recursive table merge は使わない。manifest が所有する key だけを shallow に更新する。

- method / constructor / accessor / metamethod は上書きする。
- 新 artifact にない旧 owned key は削除する。
- `__index` は stable type table 自身へ明示設定する。
- base link の metatable `__index` は新しい stable base table へ明示置換する。
- enum member は追加・更新・削除する。
- static field value が table でも、その中身を recursive merge しない。
- host、reference source、TinySystem、他 module 所有 global は変更しない。

class table 自体が instance metatable である現行表現を維持するため、table identity の保持は必須である。これにより既存 instance も次回の `obj:Method()` lookup から新 method を使う。

### 11.3 state policy

module mode の v1 policy を次で固定する。

| state/change | hot apply の挙動 |
|---|---|
| 既存 instance field | 保持 |
| method/accessor/operator body | stable type table 上で置換 |
| method/metamethod/enum member 削除 | owned key を削除 |
| 既存 static field | 値を保持。initializer の変更は live apply せず restart |
| 新規 static field | compile-time constant/default など副作用なしと証明できる initializer だけ hot apply |
| 削除 static field | owned key を削除 |
| constructor 変更 | 新しい instance にだけ反映 |
| function 値を直接 capture 済み | 古い function は更新しない。次回 table lookup から新 function |
| suspend 中 coroutine | その frame/closure は旧 code のまま |

method-body edit では同じ file の static state をリセットしない。既存 static initializer の内容を変えた場合、または新規 field の initializer が invocation/member mutation などを含み副作用なしと証明できない場合は `requiresRestart` とする。fresh VM の full snapshot load では通常どおり initializer を実行する。static table の中身を recursive merge する経路は持たない。

これは current lub の full entry hotswap が static initializer を再実行する挙動から意図的に変える点である。module mode では無関係な method edit で game/cache/host-handle state を失わないことを優先し、明示的な static initializer 変更だけを fresh VM restart の境界にする。

次は既存 instance を安全に migrate できないため `requiresRestart` とする。

- instance field / auto-property shape または initializer の変更
- record positional member の変更
- base class、type kind、runtime type identity の変更
- live instance の存在を判定できない type delete/rename

browser host は successful compile 後に player restart へ fallback できる。restart-required edit は主 500 ms SLO の対象外だが、理由を診断として明示する。

`requiresRestart = true` の batch は live registry に一部も apply しない。host は全 active artifact を link した full snapshot を fresh player iframe / fresh Lua VM へ渡す。

top-level statement は v1 では designated entry source だけに許可する。initial link では全 type の define/initialize 後に実行するが、その source の edit は entry hotswap ではなく fresh player/Lua VM restart とする。CLI の one-shot output は従来どおり対応する。

### 11.4 transaction

apply は frame safe point で batch 全体を atomic commit する。

1. revision、ABI、dependency、tombstone/ownership transfer、ownership collision を検証し、`requiresRestart` batch は mutation 前に拒否する。
2. 全 Lua chunk を revision 付き chunk name で load する。
3. affected registry record、owned type/static key、metatable、global alias を transaction log に保存する。**保存と復元は `rawget` / `rawset` で行い、own key の有無を presence sentinel として記録する。** 継承は `setmetatable(Derived, {__index = Base})` なので、通常 lookup で保存すると inherited な `Base.M` を Derived の own key として復元してしまい、以後の Base patch が Derived に届かなくなる。
4. declare → define → 証明済みの副作用なし new-field initialize を実行する。
5. 成功時だけ active revision と source map index を publish し、revision 付き commit ACK を host へ通知する(§13/§14)。
6. Lua error 時は transaction log の対象を旧参照へ戻し(存在しなかった own key は rawset ではなく削除で戻す)、last-good batch を維持する。

hot apply の initialize を compile-time constant/default など fresh value の生成・代入に制限するため、完全 rollback の対象は registry が記録する binding、type table key、metatable、global alias で足りる。既存 graph、他 module、host、I/O に触れ得る initializer/top-level code は transaction 内で実行せず restart path へ送る。stale revision は load/apply せず破棄する。

この transaction/identity の primitive は backlog T154 の `HotReload.swap` rollback と共有できるが、T154 が要求する任意の mutable global graph の復元をこれだけで満たすとはみなさない。`HotReload.swap(filepath)` は legacy API、`ModuleRegistry` は browser/module artifact の正本として区別する。lub の entry reload は引き続き `lume.hotswap` が担い、tcs の file watcher と二重に動かさない。

## 12. source map と stack trace

現行 source map は 1 Lua file 内の line number だけを key にするため、line 1 から始まる複数 artifact を区別できない。module mode では artifact-local map と revisioned chunk name を使う。

- chunk は `load(source, chunkName, "t", env)` で load する。
- chunk name は module ID、revision または artifact hash を含む。例: `@tcs/game/Player.cs@a1b2c3.lua`。
- map key は `(chunkName, luaLine)` とする。
- source path は project-relative module path とする。
- descriptor wrapper の prologue を含む実 line に mapping を合わせる。
- reload 前の closure/coroutine の stack trace 用に、module ごとの旧 map を bounded LRU で保持する。
- compatibility linker が単一 file を作る場合は従来どおり line offset を合成できる。

なお現行の lub runtime に source map の consumer は存在しない(traceback remap はなく、エラーは生成 .lua の行番号のまま表示される)。per-module map の価値を出すには lub 側 remap の feature request が別途必要であり、本節の artifact 仕様は per-module map を「生成して同梱する」ところまでを scope とする。consumer 実装は direct mode(M5)とセットで判断する。

## 13. WasmCompiler session API

現行の `CompilerExports.Compile(fullProjectJson)` は compatibility API として残し、module mode では次の stateful API を追加する。実装上は `[JSExport]` の string JSON でもよいが、毎 edit に full project と runtime prelude を往復させない。

```text
Initialize(initRequest)
  metadata/reference/fixed inputs を構築し sessionId を返す

OpenProject(sessionId, files, entryType, optionalPrebuiltManifest)
  editable project を開き projectEpoch を発行する。必要なら initial artifacts を返す。
  再呼び出しは editable set を丸ごと置換し、新しい projectEpoch を発行する
  (sample/言語切替はこの経路。session は再利用し、fixed/ref cache を保つ)

Update(sessionId, projectEpoch, projectRevision, changes[])
  changed/added/deleted/renamed files だけを受け、artifact batch を返す。
  changes[] は full text に加えて TextChange span (offset/length/newText) を受け取れる
  (span がないと SourceText 差分が whole-text change になり incremental parse が落ちる)

LinkSnapshot(sessionId, projectEpoch)
  managed 側の active artifact cache から、空 registry からも起動できる
  linked full snapshot(単一 entry Lua 文字列 + manifest)を deterministic に返す。
  bridge の full snapshot 化はこの API が正本で、JS host は linker を実装しない

Build(sessionId, projectEpoch)
  authoritative full diagnostics と全 artifact/link output を返す

Dispose(sessionId)
  Roslyn graph と cache を解放する
```

全 response は `(sessionId, projectEpoch, projectRevision)` を echo し、host は現在の epoch と
一致しない response を破棄する。sample/言語切替の in-flight response が新 project へ
届く事故を防ぐ(playground は両切替 handler から async 処理が走る)。

diagnostics は wire 上で stable な ID を持つ。fixed-binding fingerprint 違反による
edit 拒否には専用 diagnostic ID を割り当てる。ユーザーコードの文法エラーと質が違う
(「この edit は fixed library の binding を変えるため module mode では適用できない」)ため、
host が説明文言を出し分けられる必要がある。

response には phase timing を常時持たせる。

```text
parseUpdateMs
compilationUpdateMs
diagnosticsMs
complianceMs
emitMs
serializeMs
affectedModuleCount
parsedTreeCount
emittedModuleCount
cacheHitCount
managedHeapBytes (取得可能な場合)
```

request/response は monotonic `projectRevision` を持つ。compile は現在同期 API なので、host は in-flight 後に溜まった edit の中間 revision を捨て、最新 source snapshot だけを送る。response と runtime apply の両方で stale revision を拒否する。

### 13.1 runtime commit ACK

SLO の終点は Lua patch commit だが、現行 host protocol(`setFiles`/`syncFiles`/`playerReady`/`log`)には commit の成否を返す経路がなく、playground は postMessage 直後に「synced」を表示し、native も `lua_ctx_hotswap` の戻り値を捨てている。これでは stale revision reject、正しい UI 状態、edit-stop → commit の p95 実測のいずれも成立しない。

runtime apply は次の ACK を host へ返すことを module mode の必須契約にする。

```text
applyResult
  projectEpoch
  projectRevision
  ok
  commitTimeMs      (runtime 側 clock。host 側時刻との対応付けは host が行う)
  error?            (rollback 済みの失敗理由)
```

channel は host 環境ごとに選ぶ(playground bridge では Lua → JS の通知。候補: lub の host_send 相当、または player の print relay 上の構造化行。選定は lub への feature request に含める)。host は ACK を受けてから UI を `synced` に進め、benchmark も ACK 時刻を commit として記録する。

## 14. browser / lub playground integration

### 14.1 cold path

1. build 時に fixed implementation と各既定 sample の module artifact / linked entry Lua を生成する。
2. playground は prebuilt entry Lua で player を先に起動する。
3. 並行して .NET runtime と `IncrementalCompilationSession` を初期化する。
4. compiler ready 前の edit は module ごとに最新 1 revision だけ queue する。
5. session ready 後、現在の editor snapshot を open/update する。

これにより cold compile 11 秒級の待ちを初期画面から外す。

player の readiness は二段階に分ける。現行 `playerReady` は iframe script 評価直後に送られ WASM/FS の準備を意味しないため、FS 初期化前の `syncFiles` は無言で捨てられる。module mode では `playerReady`(script 評価)と `runtimeReady`(WASM main 開始 + FS 書き込み可)を分け、`runtimeReady` 前の sync は host 側で queue する。

prebuilt manifest と compiler/reference ABI の mismatch 時の挙動は build 種別で分ける。dev では fixed implementation source をその場で full compile する fallback(現行経路そのもの)へ落として開発を止めない。release / CI verify では明示的な再生成エラーにする。cs-lib はこれまで毎 compile の入力だったため編集が即反映されたが、prebuilt 化で「web 側だけ古い」staleness が新たに生まれる — fallback はこの DX regression を塞ぐためにある。

### 14.2 warm edit path

1. editor change を 75 ms debounce する。
2. changed C# source だけ `Update` へ送る。
3. compile error なら diagnostics を表示し、player の last-good code は触らない。
4. live-safe batch は `LinkSnapshot` の bridge snapshot または direct module apply payload にする。
5. `requiresRestart` batch は artifact cache だけ更新し(entry file は書かない)、full active snapshot の fresh player を二相 handoff で起動する。
6. live-safe batch は player の frame safe point で atomic apply する。
7. runtime の commit ACK(§13.1)を受けた revision だけ UI の `compiled/synced` 状態へ進める。stale/失敗 revision の ACK は捨てる。

現在の lub playground は 300 ms debounce で、毎回全 `cs-lib` と sample を `Compile` し、単一 entry Lua file を書き換えて `lume.hotswap` している。移行は二段階にする。

**bridge:** artifact cache と full snapshot への link は managed 側が持ち、JS は `LinkSnapshot` が返した最新 linked 文字列を保持して `lastLua` にするだけとする(§13。linker semantics — artifact 順序、bootstrap 埋め込み、entry return、source map 合成 — を JS に再実装させない)。snapshot は **空 registry からも起動できる full active snapshot** であり、iframe 再生成、解像度変更、`requiresRestart` 後も unchanged/fixed module が欠落しない。warm hotswap 時は同じ snapshot 内の unchanged descriptor を artifact hash で skip する。compile(`Update`)自体が返すのは delta のままである。

bridge entry の実行契約は次で固定する。

- process-global `_G.__tcs_runtime` / registry を ABI guard 付きで一度だけ作り、reload ごとに再生成しない。
- 現行 `LuaRuntime.CreateEmbeddedPrelude` を毎 reload 実行せず、module mode 専用の idempotent bootstrap を使う。
- **entry が `return` するのは薄い stable wrapper とする**: onInit/onFrame 等の function だけを持ち、static field を含む type table を直接参照させない。`lume.hotswap` の `update(oldmod, newmod)` は同一 identity でも entry table から到達可能な全 table(metatable 込み)を再帰走査するため、type table を直接返すと走査コストが live state 量(static list に数十万 entry を持つ sample が実在する)に比例して非有界になる。function 値は走査対象にならないので、wrapper でこの走査は O(キー数) に落ちる。
- 併せて lub 側 `lume.hotswap` の `update` に `old == new` fast-path を入れる feature request を出す(防御の二重化。なお fast-path は「契約違反時に lume の visited メモ化が偶発的に registry table を merge から守る」挙動を消すが、識別子 identity が維持される契約下では global diff 自体が発火しないため安全)。
- entry wrapper、runtime type table、`_G` legacy alias はいずれも reload をまたいで同じ identity を保つ(identity が変わると lume の global diff が旧 table へ merge して registry ownership と衝突する。しかも merge されるかは entry からの到達可能性に依存し、壊れ方が非決定的になる)。
- `applyBatch` 失敗は registry 内部 rollback を完了させてから Lua `error` として `lume.hotswap` へ伝え、old module ref を維持させる(lume の onerror は `_G` を shallow restore するだけで、nested な mutation は戻せない)。
- commit 後は失敗し得る処理を置かず、commit ACK(§13.1)を送って直ちに stable entry wrapper を `return` する。
- bootstrap/global binding が同じ object のままなので、lume 側の global recursive update に registry/type graph を再生成させない。

この bridge により既存の単一 entry file / mtime / `lume.hotswap` 経路を維持できる。MEMFS の mtime は ms 解像度のため、同一 ms 内の連続書き込みは poll に取りこぼされ得る。snapshot は revision を含み内容は毎回変わるので、host は一定時間 commit ACK が来ない revision を entry 再書き込みで retry する(ACK 導入によりこの取りこぼしは検出可能になる)。

`requiresRestart` の fresh player 起動は **二相 handoff** にする。現行 `restart()` は旧 iframe を先に削除するが、module mode の restart path は static initializer / top-level code を実行するため、compile 成功後の runtime error で last-good player まで失う。hidden の fresh player を full snapshot で boot し、`runtimeReady` + 初回 commit ACK を確認してから表示を swap し、失敗時は旧 player を残して診断を表示する。

playground editor は file の add/delete/rename UI を持たない(固定ファイルセットの content 編集のみ)。§7.3 / §8.1 / §10 の file add/delete/rename、tombstone、ownership transfer は v1 では CLI・テスト経由で受け入れ、playground からの検証は editor 拡張時に行う。

**direct:** player が changed artifact batch を直接受けて registry に apply する。entry file 全体の再書き込みをなくせるが、`../lub` 側の feature request が必要である。

.NET runtime の dedicated Worker 化は main thread stall を避けるため望ましい。ただし現状は worker 内の `dotnet.create()` 完了問題があるため、500 ms SLO の前提には置かない。まず main thread でも 275 ms 以下になる増分構造を作り、worker は原因解消後に追加する。Roslyn 自体は worker 内でも `concurrentBuild: false` の single-thread とする。

## 15. 性能予算と benchmark

| 区間 | engineering budget |
|---|---:|
| edit-stop → compile request（debounce） | 75 ms |
| delta JSON → module artifacts（managed compiler + serialization） | 275 ms |
| link/transfer → Lua atomic commit | 75 ms |
| GC/frame wait/揺れの余裕 | 50 ms |
| **計画値** | **475 ms** |

各区間の p95 を加算して end-to-end p95 を数学的に保証するものではない。表は実装判断用の budget であり、release gate は同じ edit revision を通した end-to-end p95 < 500 ms の実測とする。

正式 benchmark は実ブラウザの Release build で行う。

- 5 回以上 warm-up 後、同じ edit sequence を 30 回以上測る。
- small / representative / largest playground sample を含める。
- method literal change、method body dependency change、syntax error→recovery、surface change を別 series にする。
- **player 側 apply の series を独立に持つ**: full snapshot の Lua load(parse)、`lume.hotswap` 経路の走査、applyBatch の unchanged-skip path を、live state が大きい sample(static list に数万〜数十万 entry)でも測る。native Lua 5.5 の baseline は `bench/player-apply.lua`(T172): load は 156 KB で p50 5.0 ms、471 KB で p50 16.7 ms と budget 内。一方 type table を直接 return した場合の lume 走査は sprite 10 万で p50 106 ms、20 万で p50 224 ms と **native ですら 75 ms budget を単独で超える**。thin wrapper と old==new fast-path はいずれも 1〜4 µs 台に落ちる。§14.2 の wrapper 対策は必須であり、browser(WASM)では同系列を実測して係数を確定する。
- end-to-end は edit-stop から runtime commit ACK(§13.1)までを同じ revision ID で対応付けて測る。postMessage 送信時刻を commit と見なさない。
- browser `performance.now()` と managed phase timing を同じ revision ID で対応付ける。
- **main-thread 同期 compile の UI 影響を併記する**: Long Task 時間、input delay、editor frame gap。Worker 化前は「latest-wins queue」が実質機能しない(同期呼び出し中は event loop が止まる)ため、SLO を満たしても体感停止が残る可能性を数字で見る。
- 測定条件を明記する: foreground tab、60fps、rAF throttling なし(mtime poll は frame begin で走るため、commit は最大 1 frame 遅延する)。
- p50/p95/max、artifact bytes、affected module count、heap trend を記録する。
- body edit では `parsedTreeCount = 1`、`emittedModuleCount = 1`、fixed tree parse/emit = 0 を assertion にする。
- 1,000 edit の soak で compilation history が無制限に残らず、GC 後 heap が plateau することを確認する。
- **sample/言語切替の soak を別に持つ**: `OpenProject` 張り替え(epoch 更新)の繰り返しでも heap が plateau することを確認する。playground の実 workload は同一 project の連続 edit だけではない。

surface change は v1 で全 editable module を再生成するため、fan-out に比例する。現在の playground workload では別途 p95 を記録するが、主 500 ms gate は method-body edit とする。surface change の実測が UX 上問題なら §8 の candidate-aware invalidation を次段で行う。

構造変更後も managed compile p95 275 ms を超える場合に限り、phase profile に基づいて次を検討する。

1. compliance walker の統合
2. JSON source-generator / payload の縮小
3. profile-guided or full AOT の A/B
4. Worker 化

AOT は bundle size/cold load と交換になるため、計測なしには採用しない。

## 16. compatibility

既存 API を直ちに壊さない。

- `Transpiler.Transpile*` は ephemeral session で全 source module を生成し、`ModuleLinker` で従来の単一 Lua string にする。
- CLI default output、runtime/prelude 埋め込み、`--entry`、`--sourcemap` を維持する。
- entry type は T155 と同じ symbol-to-emitted-name 解決を使い、raw input string を Lua に直接足さない。
- module artifact の `_G` alias は legacy compatibility のみとし、正本 identity/ownership は registry に置く。
- `HotReload.swap(filepath)` は legacy wrapper として残せるが、module browser path は `applyBatch` を使う。
- authoritative CLI/check/CI は full diagnostics のままにする。

module ABI 公開前に T151 の中央命名、T155 の entry 解決を統合する。T149 の source-order-independent inheritance は declare/define 分離で満たし、T154 の transactional rollback は registry transaction と共通化する。**T149/T151/T154/T155 はいずれも未着手**であり、本設計の前提としてこの 4 task 分の積み残しがあることを実装計画に織り込む。

現行 emitter は class を素の global 代入(`Hello = {}`、local + alias 分離なし)で束縛しており、registry 所有 + read-only module `_ENV` への移行は「`_G` alias を legacy に格下げする」以上の emitter 改修である点も見積もりに含める。

native `.csproj` 経路(`tcs --watch` の単一 file atomic write + lub の mtime poll)は bridge と同一の配送契約を持つ。CLI watch の出力を bridge-style snapshot(idempotent bootstrap + descriptor)へ切り替えれば、native と web で hot reload semantics(method edit で static 保持、initializer 変更で restart)を統一できる。現状のままでは同じ C# ソースが native(full re-require で static 再実行)と web module mode(static 保持)で挙動が割れるため、M4 以降の切り替えを推奨する(CLI の非 watch 出力は従来のまま)。

## 17. 実装フェーズ

### M0: benchmark と契約 test

- Chrome benchmark harness と phase timing を repository に置く。
- 現行 full path、sample-only path、memory soak の baseline を採る。
- **player 側 apply の baseline を採る**: full snapshot Lua load、`lume.hotswap` 走査(live state 大の sample 含む)、applyBatch skip path(§15)。
- incremental/full diagnostics differential test を先に作る。

### M1: stateful Roslyn session

- fixed/ref tree の init cache。
- `WithChangedText` / `ReplaceSyntaxTree` 更新。
- body-only diagnostics と semantic surface classifier。
- error head / last-good artifact の分離(dirty closure 込み、§7.3)。
- 既存 emitter を 1 source module ずつ呼べる internal API。
- body-only fast path の semantic 診断は変更 body span 限定の `SemanticModel.GetDiagnostics(span)` を使う(body edit の新エラーは編集 body 内にしか現れない)。
- 変更 method だけを emit して cache 済み module Lua へ差し替える method 単位 splice(`LuaEmitter.MethodRanges` / `EmitSingleMethod`)。file 全体の再 emit は 1 sample ≒ 1 file の実 workload で budget を超えるため必須だった。constructor/accessor/record/警告持ちは file 全体 emit へ fallback する。continue ラベル採番だけ file 内通番と一致しないが、Lua のラベルは関数スコープのため実行意味は同一。
- browser benchmark 用の最小 `OpenProject/Update` JSExport。production wire contract は M4 で確定する。

この段階の gate は実ブラウザ warm compiler p95 275 ms 以下、body edit の parse/emit tree 数 1 である。**この gate は go/no-go であり、通過するまで M2 以降に着手しない。** 2026-07-14 に `bench/chrome-session.mjs`(cs-lib 20 files + sample + probe、153 KB)で通過を確認: 実サンプル級 file(11.5 KB / 61 methods)の warm body-edit p50 105.8 ms / p95 124.2 ms(phase p50: parse 2 / semantic 19 / compliance 23 / emit 1 ms)、極小 file p50 8.2 ms。 `SemanticModel.GetDiagnostics()` は変更 span ではなく tree 全体の全 body を再 bind するため、1 sample ≒ 1 file の現 workload では body edit でも実質全 editable の診断 + compliance + emit が残る。自己測定(editable-only warm 462–527 ms)から 275 ms へは 4〜5 割の削減が必要で、未達なら §15 の追加施策(compliance walker 統合、payload 縮小、AOT A/B、Worker)を M1 内で消化し、それでも未達なら設計を見直す。

### M2: descriptor artifact / registry vertical slice

`bench/m2-registry-spike.lua`(実 emitter 出力 + registry/read-only `_ENV` プロトタイプ)で
2026-07-14 に検証済み: define chunk は現行 emit 出力から宣言行(`Name = {}`)と static
初期化行を除くだけで registry env 下で動き、identity 維持・method swap・static 分離・
base relink・owned key 削除が成立する。emitter の expression/statement 層は無傷で流用できる。
また cs-lib / サンプルは全て flat 名のため、T151/T155 は本 milestone の hard blocker ではない
(namespaced 入力の対応時に統合する)。

- `ModuleArtifactBatch` / tombstone / deterministic module ID / ABI hash。
- declare(pre-zero 込み)/define/per-type initializers descriptor emit と read-only module `_ENV`。
- stable type table を作る最小 `ModuleRegistry`。
- legacy single Lua output の最小 linker。
- T151/T155 naming/entry contract の統合。

### M3: transaction / full snapshot compatibility

- per-module source map と revisioned chunk name。
- fixed implementation の build-time pre-emit。
- ownership-aware shallow patch と deleted key cleanup(rawget/rawset + presence sentinel)。
- stable type identity、inheritance relink、state policy。
- atomic batch commit / rollback / stale revision reject / commit ACK。
- restart classification と fresh-VM full snapshot linker(`LinkSnapshot` の中身)。
- idempotent runtime bootstrap、thin entry wrapper、lume bridge handshake。
- T149 の acceptance と T154 のうち registry-owned transaction cases を取り込む。legacy `HotReload.swap` の任意 global graph rollback は別 gate とする。

### M4: Wasm delta API と playground bridge

2026-07-14 に warm 経路の gate を通過。`bench/chrome-e2e-ack.mjs`(lub playground
実機、17_flappy C#、body edit)で edit-stop → commit ACK p50 422 ms / p95 442 ms
(< 500 ms)。cold start(session open + LinkSnapshot + player boot)は 11.7 s で、
prebuilt assets(下記残項目)導入までは従来の full compile(約 5.6 s)より遅い。
実装済み: SessionExports の Open(projectEpoch)/Update(restart 分類込み)/
LinkSnapshot(raw Lua)、lub 側 session client + 75 ms debounce + ACK 駆動 status +
ACK timeout 再送、lume.hotswap の `old == new` fast-path、headless verify A6
(実 C# edit → commit ACK 貫通判定)。

同日中に cold path 残項目も完了: prebuilt snapshot(tcs CLI `--snapshot` +
lub `gen-tcs-prebuilt`。cs-lib + sample を build 時に link した bridge snapshot
で player を先に起動し、session は background で温める)により cold start は
11.7 s → **0.5 s**(status running まで。従来 full compile 比でも 11 倍)。
runtimeReady 分離(player 側で FS ready を poll し、それ以前の syncFiles は
queue へ)、compiler warming 中の edit queue(latest-wins、ready 時 flush)、
requiresRestart 編集の二相 handoff(hidden player を boot し初回 commit ACK
確認後に swap、失敗時は旧 player 維持。実測 7.4 s)も実装。

設計からの簡略化: prebuilt は boot 加速専用とし、session への artifact 再利用は
しない(session open は常に editor ソースを full compile する)。これにより
ABI-mismatch fallback は不要になる — prebuilt が古くても最初の編集で
authoritative な snapshot に置き換わり、release では CI が生成するため
常に一致する。TextChange span wire も見送り(MinimalChange で parse p50 2 ms、
span 化の効果が計測誤差未満)。


- `Initialize/OpenProject/Update/LinkSnapshot/Build/Dispose` JSExport(projectEpoch、TextChange span、diagnostic ID 込み)。
- fixed/sample prebuilt assets と background prewarm、dev の ABI-mismatch fallback。
- latest-wins queue、75 ms debounce、last-good behavior。
- 単一 entry Lua compatibility bridge、commit ACK channel、二相 restart handoff、runtimeReady。
- edit-stop → commit **ACK** p95 500 ms gate(headless verify にも「実 C# edit → compiler ready → commit ACK」を貫く判定を追加する。prebuilt 起動により「compiler ready 前の絵」で全サンプル切替が PASS する偽陽性が新たに生まれるため)。
- **lub 側 integration change 一覧の doc 化**。本設計の consumer 変更は 1 件の feature request に収まる規模ではない(tcs-compiler.ts の session client 化、main.ts の cold path 逆転 + ACK 駆動 status + restart 分岐、gen-tcs-assets の fixed artifact/prebuilt 生成、lume fast-path patch、verify-headless の ready/commit signal 対応、docs/manual の semantics 変更記載)。

`../lub` への変更は feature request として提出し、この repository から直接編集しない。

### M5: optional optimization

- direct artifact apply integration。
- candidate-aware dependency invalidation。
- Worker 問題の解消。
- profile が必要性を示した場合だけ AOT A/B。

2026-07-14 の profile 判断: warm E2E p95 442 ms(gate 500 ms)を bridge のまま
充足しており、いずれも着手しない。内訳 — direct apply: entry 全文再送 +
hotswap 再評価は実測で warm 経路の支配項ではない(managed compile p50 45 ms、
snapshot 再評価は parse 数 ms + registry skip)。candidate-aware invalidation:
slow path は restart 分類の編集でしか発生せず SLO 対象外。Worker: prebuilt boot
により起動は非ブロッキング化済みで、残る main-thread stall は background open
の約 12 s のみ(dotnet worker 問題の解消待ち)。AOT A/B: managed 予算 275 ms に
対し p50 45 ms で不要。

## 18. 必須テストと受入条件

### 18.1 compiler correctness

- body-only edit は changed module だけ emit し、full compile と同じ Lua 実行結果になる。
- `internal` / extension / overload / operator / const surface change は全 editable module を invalidate する。
- **parameter 名変更(named argument)と field/property 型変更**は surface change として全 editable module を invalidate する。
- **変更 span が body に完全包含されない edit は surface hash 不変でも slow path に落ちる。**
- modifier/attribute change を surface change とし、fixed-binding fingerprint を変える editable declaration を拒否する(専用 diagnostic ID で)。
- syntax/semantic error 中は artifact なし、修正後は last-good surface から正しく再判定する。
- **cross-file の error→recovery**: A が error の間に B を編集し、A だけ直して復帰した revision で B の artifact も dirty closure により再 emit される。
- file add/delete/rename と複数 type/file。
- deleted module tombstone と同一 batch の ownership transfer。
- interface-only / reference-only source は Lua を生成しない。
- fixed implementation は editable update で parse/emit されない。
- source/file 順を逆にした継承と複数段継承。
- incremental diagnostics と full build diagnostics の parity。
- deterministic artifact bytes/module ordering。

### 18.2 runtime correctness

- method update 後も class table と既存 instance metatable の identity が同じ。
- method / metamethod / enum member の削除が反映される。
- method-body edit で既存 static scalar/table が保持される。
- static field 追加の pure initializer、field 削除、initializer 変更の restart classification。
- **pre-zero と per-type topo initialize**: 同一 file の型 A,C と別 file の型 B で A→B→C の初期化順が成立する。cycle 内で先読みされた static は nil ではなく型 default を返す。
- unchanged module と host/reference/TinySystem global は変更されない。
- base link の追加/削除/変更は live apply 前に restart になる。
- dependent artifacts を含む batch が全成功または全 rollback になる。
- owned type table の self-reference/cycle、metatable、global alias を変更後に error が出ても rollback する。
- **failed override-add の rollback 後、base method の更新が derived の lookup に届く**(inherited 値を own key として復元していないこと。rawget/rawset + presence sentinel の検証)。
- ownership 外へ触れ得る initializer と top-level edit は transaction に入らず fresh VM restart になる。
- stale revision を apply しない。commit ACK が (projectEpoch, revision, ok) を正しく返す。
- module 間 type move の ownership transfer と collision error。
- multiple module/revision source map が正しい stack frame を引く。
- existing function capture / coroutine の更新境界が文書どおり。
- **`lume.hotswap` 経由の end-to-end**: bridge snapshot を実際に `lume.hotswap` で reload し、(1) entry wrapper / type table / `_G` alias の identity 維持、(2) applyBatch 失敗時に registry と `_G` の両方が last-good に留まる、(3) 同一 identity 維持下で lume の recursive update が live state を走査しない(live state 大の sample で時間上限 assertion)、を確認する。registry 単体テストでは代替できない — lume の onerror shallow restore と visited メモ化(merge されるかが entry からの到達可能性に依存する)は lume を通してしか検証できない。

### 18.3 compatibility / performance

- 現行 semantic test、CLI、`--entry`、runtime prelude、source map test が通る。
- linked single Lua が現行 host で load/hotswap できる。
- linked full snapshot だけで空 registry / fresh player を再構築できる。
- prebuilt initial sample は Roslyn ready を待たずに起動する。
- warm method-body edit の browser p95(edit-stop → commit ACK)が 500 ms 未満。
- managed compile+serialize p95 が 275 ms 以下。
- 1,000 edit soak で heap が edit 数に比例して増えない。
- sample/言語切替(`OpenProject` 張り替え)の soak でも heap が plateau する。
- headless verify が「実 C# edit → compiler ready → commit ACK → 画面変化」を貫いて判定する(prebuilt の絵での偽陽性がない)。
- ABI mismatch 時、dev は fixed source の in-browser full compile へ fallback し、release/verify は明示エラーになる。

## 19. 採用しない案

### 毎回 full compile のまま AOT

interpreter cost は下げ得るが、固定 source の full diagnostics/compliance/emit と巨大 payload を残す。構造改善後の追加施策にする。

### fixed implementation を毎回入力し、Lua だけ cache

emit は減っても full parse/diagnostics と JSON 転送が残る。Roslyn base compilation と tree 自体を session に保持する。

### reference source を metadata reference だけにする

現行 emitter は reference source declaration を `--ref` runtime type と識別し、object initializer や out multi-return の lowering に使う。単純な metadata 化はこの ref-only 意味を失うため、専用 ABI metadata を設計するまでは reference source tree を init cache する。

fixed implementation は prebuilt Lua + metadata assembly に分ける余地がある。ただし same-compilation の `internal` visibility、symbol/runtime type ID、diagnostic parity が変わるため v1 では source tree cache を選び、profile が必要性を示した後に独立して A/B する。

### 1 type = 1 module

既存 source layout を性能のために強制し、複数 type/file と enum/interface の扱いを複雑にする。source path を安定した所有単位にする。

### reverse dependency graph だけで signature change を絞る

新 overload/extension/operator candidate は旧 graph に edge がない。v1 は全 editable module、後段は candidate-aware graph とする。

### `_G` の recursive deep merge

deleted member、base link、static table、ownership、rollback の意味が不明確になる。manifest に基づく scoped shallow patch にする。

### remote compile service

playground の client-only / offline-capable 方針と異なり、network latency と運用依存を増やす。本設計の対象外とする。

## 20. 関連文書

- [プロジェクト目的](../objective.md)
- [現在の実装状態](current.md)
- [browser-wasm feasibility spike](wasm-playground-spike.md)
- [lub script 層ギャップ分析](lub-gap-analysis.md)
- [未実装タスク](tasks.md)

本書は将来設計であり、現行機能の記録ではない。実装開始時に task 番号を割り当て、TDD、`done.md` / `tasks.md` / `current.md` 更新、1 task 1 commit の通常 workflow に乗せる。
