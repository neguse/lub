# hot reload 設計 — ユースケースと状態移行の統一契約

本文書が hot reload の正本で、live と restart の分類、状態の移行、hook、host との契約を定める。dev (tcs VM) が計画を当てる仕組みは vm-design §12、計画の byte 列と共有の walker は object-model §6、§7。

## 1. ユースケースと受入条件

tcs は dev (tcs VM) / release (C) の 2 backend 構成。**1st priority は platform × mode の 2×2 = Windows dev / Windows release / web dev / web release**。実機 (Playdate 級) は 2nd priority で、KPI floor (perf/README) は性能下限としてのみ効かせる。

hot reload の目的はゲーム開発の反復 (挙動調整・数値チューニング・データ形状の試行錯誤) をシーンを作り直さずに回すこと。dev backend の reload (§4-§9) と、release backend の reload (§3 — 技術オプションを提示、採否は D6) は機構が異なるため分けて扱う。

| UC | 環境 | ループ | 受入条件 |
|---|---|---|---|
| UC1: Windows dev | Windows 上の C ホスト (lub 等) に組み込んだ tcs VM | Rider で C# 編集 → driver (`tcs vm --watch`) が compile と差分 → ホストがフレーム境界で batch を当てる (`tvm_apply_batch`) | method 編集が次 frame から見える。field 追加/削除でシーン状態 (instance の生存値) が保持される。適用は frame 境界で、失敗時は旧状態のまま続行。restart 判定時は理由を表示してシーン再起動導線へ |
| UC2: web dev | browser の wasm の tcs (playground の VM の増分 session) と player の tcs VM (wasm) | エディタ編集 → 増分 session → player が batch を当てる | UC1 と同じ分類 (§5) と適用の約束 (§7)。編集から反映までを対話の速さに保つ |
| UC3: Windows release | `tcs c` → C → ネイティブ静的リンク | reload の採否は D6 (§3 のオプション) | L1 以上を採る場合は §3 の該当受入条件。L0 なら reload なし |
| UC4: web release | `tcs c` → C → wasm (emscripten 系 toolchain) | 同上 (D6) | 同上。C 出力が wasm target でビルド可能であることは release 側の課題で本設計の対象外 |
| (2nd) 実機 dev | Playdate 級実機 | PC で編集 → 転送 → 実機で適用 | VM が 10ms 予算に収まる範囲は UC1 の延長 (ホストの義務が §9 の通り小さいことが条件)。収まらないゲームでは release reload (§3 L1) が実機イテレーションの前提になる — D6 の判断材料 |

restart が正常系である理由 (§4) も UC で決まる: dev ループで restart は「シーンをもう一度作る」コストを意味するので、live にできる編集はなるべく live に倒したい。ただし移行の正しさを犠牲にはしない — 移行が証明できない編集は検出して restart にし、理由を表示する。

## 2. dev 機構の中立性契約

dev backend の reload 機構が backend 間の意味論一致 (digest gate) を壊さないための契約。release 自体の reload (§3) とは独立に成立させる:

- **reload の機構 (計画の適用、hook の呼び出し) を C の出力は持たない**。release の reload は §3 の機構で別途実現する (採る場合)
- **reload の機構は observable semantics に影響しない**。VM は生きている object を heap から列挙するので (vm-design §12)、構築ごとの登録のような定常の仕事が無く、reload を当てない実行は reload の無い実行と同じ。差分ゲートはこの実行を C と .NET と比べる
- **`OnReload` hook は「reload を持つ実行系」でのみ発火する**。L0 release では普通の method のままで、どこからも呼ばれない。ゲームロジックを hook に置くと実行系間で挙動が割れるため、用途は「reload 起因の derived data 再構築」に限る
- **reload で作られた状態はセッション限り**。cold start との互換 (セーブデータ等) は migration の要求に含めない
- **性能**: reload の費用は当てるときだけかかり、heap の大きさに比例する (vm-design §12.2)。dev 専用のヒッチとして許す (§9)

## 3. release backend の reload — 技術オプション (D6)

release (AOT C) の reload は「するかどうか」ではなく「どのレベルを採るか」の選択。レベルは独立に採否でき、S は直交。

### L0: reload なし

編集は常に再ビルド + 再起動。追加コストゼロ。

### L1: whole-module code swap (dll / wasm instance 差し替え)

- **対象編集**: method body 系のみ。layout 変更 (§5.2 で restart になる類 + field 追加/削除) は restart
- **機構**: script 全体を 1 つの動的 module (Windows: dll、web: wasm instance) にする。状態 heap は host 側 allocator が所有し swap を跨いで生存。statics は swap 時に metadata diff で旧→新へ値コピー (dev の retained 規則と同じ)。**旧 module は unload しない** — 状態内に残る関数ポインタ (捕捉済み closure / method group thunk) は旧 code を指し続けて安全に動く。dev の VM は生きている closure を新版の関数へ付け替える (vm-design §12.4) が、L1 では捕捉済みの関数値は旧 code のまま走る
- **前提**: C 出力の動的 module ビルド (静的 link 用の `tcs c --lib` はある — 動的化は entry 関数表 + statics の export 列の追加)、type_id の版間安定化 (metadata からの安定採番 — 今の DFS 範囲の `is` と要調整)
- **コスト**: 定常オーバーヘッドほぼゼロ (module 内は直接呼び出しのまま)。メモリは旧 module 保持ぶんセッション内で線形に増える
- **受入条件**: method 編集が次 frame から見える。restart 境界は dev より広い (shape 系は全部 restart) が、判定と理由の表示は dev と同じ規則 (`ReloadRules`) と計画 (`ReloadPlan`)

### L2: L1 + shape migration (CLOS on C)

- **追加機構**: 型記述子と共有の walker (object-model §4、§7。C の runtime も同じものを持つ) で heap を辿り、layout の変わる object を作り直して pointer を張り替える (VM の reload と同じ仕事。vm-design §12.2)、type_id の付け直し
- **コスト**: 実装規模は最大。release の性能への影響は perf harness で実測して決める
- dev と同じ編集分類 (§5.2) を release でも成立させる唯一のレベル

### S (直交): snapshot / restore による高速 restart

- 状態を直列化して保存 → 新 binary 起動 → 復元。live ではないが shape 変更も一様に扱え、実装は L2 より遥かに小さい (直列化メタデータは型記述子が既に持つ)。L1 と組み合わせると「method 編集 = live、shape 編集 = snapshot restart」の二段になり、体感は dev reload に近づく
- web release の更新 (live-ops 的な差し替え) にも流用できる

### struct の位置づけ (レベル共通)

サブセットに `ref` / `in` が無く (out は呼び出しの中で書くだけ)、interior pointer が存在しないため、struct 値は常に owner (変数 / field / 要素) への値埋め込みで identity を持たない。したがって release reload における struct の増分難度は小さい: L1 では layout 編集は元々 restart 対象、L2 でも作り直しと張り替えの対象は class instance 側で struct は owner の作り直しに随伴するだけ、S では型記述子で直列化が閉じる。C backend が struct を C 値型に落とすこと自体は reload をどのレベルでも難しくしない。

### 判断材料 (D6)

- Windows dev / web dev では dev backend の reload で開発ループが成立するため、1st priority の範囲では release は L0 でも開発体験は欠けない
- release reload が効くのは: (a) release でしか性能が出ない対象での iteration (実機級 — 2nd priority だが §1 の通り実機 dev の前提になり得る)、(b) 長い再現手順を要する終盤チューニングを release build で行う場合、(c) web live-ops 的な更新 (S で足りる可能性が高い)
- 推奨順: まず L0 で 1st priority を成立させ、L1 は「dll/wasm module 化 + statics export」という前提部分だけ C backend の設計に織り込んでおく (後から L1 を足すときに出力契約を壊さないため)。L2 は実機需要が確定してから

## 4. 設計原則

**すべての編集は (a) 完全な移行カバレッジを証明できる live apply か、(b) requiresRestart のどちらかに分類される。無言の部分移行は存在しない。**

dev の VM でこの原則が成り立つ根拠は次の 3 つ:

1. **生きている状態の列挙** — VM は heap の全 object を型記述子付きで辿れる (vm-design §10、object-model §7)。生存 instance と、struct の値の居場所 (field、配列 / List の要素、Dictionary の値、cell、static) を漏れなく見つけるので、instance と struct の形の変更を live にできる
2. **失敗の経路を反映の前に置く** (§7) — 読み込みと計画の検査が通るまで状態を変えず、反映の段には失敗の経路が無い
3. **restart の検出** (§6) — compiler の規則 (§11) と VM の検査が、移行できない差分を restart にして理由を返す

## 5. 状態カバレッジと編集分類 (dev)

### 5.1 状態カバレッジモデル

migration が到達できる生存状態の全域 (roots):

| root | 到達方法 | 移行 |
|---|---|---|
| class / record / struct の static field | static の表を名前で引く | 型が同じなら値を保つ。struct の static は struct の計画で組み直す |
| 生存 instance の field | heap の列挙 | 計画の field の差分 (retained / added / reset) |
| struct 値 (instance / static の field、配列 / List の要素、Dictionary の値、cell、任意のネスト) | 持ち主の型記述子 | 新しい layout へ組み直す |
| 実行中 frame の local / 引数 | — | 対象外 (フレーム境界でだけ当てるので VM の frame は無い) |
| closure の捕捉 | 捕捉される変数は cell (heap の object) | 捕捉した instance と struct の値も上と同じく届く |
| closure と method group の関数 | closure の object | 新版の対応する関数へ付け替える (vm-design §12.4)。対応が無ければ restart |
| host の handle | handle の表 | 作り直した object を指すよう張り替える |

前提: yield / iterator / coroutine は TCS1001 のため、フレーム境界を跨いで生存する frame は無い。

### 5.2 編集分類マトリクス

| 編集 | 分類 | 規則 |
|---|---|---|
| method / accessor / operator / constructor の body | live | 新版の関数表に入れ替える。消した method は消える |
| lambda の body | live | 生きている closure は vm-design §12.4 の対応で新しい body を走らせる。対応の付かない生きている closure があれば restart |
| instance field / auto-prop の追加 | live | 生きている instance を作り直し、新版の initializer の値 (無ければ型の 0 の値、struct は zero 値) を入れる |
| instance field の削除 | live | 作り直した instance に載らない |
| instance field の同名型変更 | live | 新しい型の initializer の値か 0 の値に reset (値の再解釈はしない) |
| instance field の改名 | live (値は移らない) | 削除と追加として扱う (`[RenamedFrom]` による引き継ぎは無い。§11) |
| instance field の initializer の変更 | live | 生きている instance は今の値のまま、新しく作る instance から効く。host に知らせる (`reload_notes`) |
| struct layout 変更 | live | 持ち主の型記述子で組み直す。struct in struct は再帰 (例外は §11 の既知の制限) |
| record / record struct の positional member 変更 | live | class / struct の field と同じ規則 (等価は新しい field 集合で比べる) |
| static field 追加 | live | 新版の initializer を走らせる (持ち主の型が遅延初期化でまだ初期化していなければ、最初の読み書きで全部を初期化する)。initializer が無ければ型の 0 の値 |
| static field 削除 | live | 消える |
| 既存 static の値 | live | 保持 (再実行しない) |
| 既存 static の initializer 変更 | live | その static だけを新版の initializer で評価し直す。ほかの static と heap は保持する |
| 既存 static の型変更 | live | その static を新版の initializer で評価し直す (initializer が無ければ型の 0 の値) |
| const の値変更 | live | 使う側には埋め込まれる。const 自身も評価し直す |
| 評価し直す static や const を initializer で読む static | live | 読む先を辿って、同じく評価し直す。辿るのは initializer の式が直に名前を引く static と const で、呼んだ関数の中の読みは辿らない |
| 既存 static の initializer の削除 | **restart** | 評価し直す式が無く、保持した値は旧い initializer のまま |
| base class 変更 (再親付け) | **restart** | 継承 field 集合と ctor 連鎖の差分移行は対象外。class / record の継承は TCS1001 で書けない (q.md Q15) |
| class / record / struct / enum の追加 | live | 新規定義のみ |
| type の削除・種類の変更 | **restart** | 生きている instance の有無を見ない (§11) |
| enum member 追加 | live | 保存済みの値はそのまま |
| enum member の削除・renumber | **restart** | 保存済み int 値の再解釈は不能。enum member の列を metadata に持って検出する |
| top-level statement の編集 | 実行中の状態に効かない | top-level 文は entry の `Main` の本文で、reload は `Main` を走らせ直さない |
| interface | live | 状態を持たない |

## 6. restart の検出

restart は 2 か所で決まる。

1. **compiler** (`ReloadRules`、`SelfHost/Tool/ReloadRules.cs`): 旧版と新版の IL の metadata (型の種類、base、static と const の型と initializer の hash、enum member) を比べる。規則は §11 の表
2. **VM** (`tvm_apply`、vm-design §12.3): 生きている object の型が新版に無い、生きている closure の関数に同じ形 (引数・戻り値・捕捉の種類と型) の対応が無い、旧版が string の定数を持つのに新版に string の型が無い

VM は struct の値の居場所を型で辿れるので、closure の捕捉や容器の中の struct の値を理由に restart にすることは無い。VM の検出は旧版の状態に対して行う — 生存状態は旧版のプログラムが作ったものだから。

## 7. 適用の段 (atomicity)

1. **検査** (状態を変えない): 新版の読み込みと検査、計画の restart の理由、生きている object と closure の対応 (§6)。どこかで止まれば旧版のまま続く
2. **反映** (失敗の経路が無い): layout の変わる object の作り直し、型と関数の付け替え、static の移し、module の入れ替え、参照の張り替え
3. **新版の関数を走らせる**: 足した field と型を変えた field の initializer、足した static と評価し直す static の initializer、続いて hook (§8)

initializer は新版の関数なので、型と heap が新版で揃った 2 の後に走らせる (旧版の heap のまま走らせると `is` と cast が型と合わない)。3 の fault は commit を巻き戻さず警告にする。fault した initializer の field は 0 の値のまま。

## 8. hook

| hook | 対象 | 契約 |
|---|---|---|
| `void OnReload()` (instance method) | `OnReload()` を持つ型の生きている instance ごとに 1 回 | 反映と initializer の後。順序は不定 |
| entry の型の static `OnReload()` | reload の commit ごとに 1 回 (最初の読み込みでは呼ばない) | derived data (mesh 等) の再構築用。instance hook の後 |

hook の fault は警告で、reload は巻き戻さない (`tvm_apply` は `TVM_OK` で、`tvm_error` が警告を持つ)。

## 9. アーキテクチャとホスト統合契約

```
v1 の IL ──┐
           ├─ ReloadRules (restart の理由) + ReloadPlan.Compute (型と static の計画)
v2 の IL ──┘        │      ── 分類 (live の batch / requiresRestart + 理由)
                    ├─ VM (tvm_apply / tvm_apply_batch — UC1、UC2、実機 dev)
                    └─ (D6 で採る場合) release (L1: module swap 計画 / L2: 作り直しの計画)
```

- 分類と移行の計画 (field の差分、struct の組み直し、restart の理由) は `ReloadRules` と `ReloadPlan` に一本化し、適用する側は計画を受けるだけにする。release の編集分類 (L1: method body のみ live) も同じ出力から導く
- metadata は IlExport (class / struct / enum の field、static と const、enum member、initializer の hash) と、module の型記述子を正とする
- **ホスト統合契約**: ホスト側の義務は「受け取った batch をフレーム境界で当て、restart なら module を読み直す」のみ (vm-design §13.2)。旧版の保持・差分の計算・restart 判定・batch の生成はすべて driver 側 (UC1 では同一マシンの `tcs vm --watch`、UC2 では browser の増分 session)。この自己完結性が実機 dev への延長条件
- **watch の stream (UC1)**: `tcs vm --watch` は標準出力へ `@@tcs_<kind> <UTF-8 の byte 数>\n` の行と本文の組を順に書く (kind は `vm_batch`、`reload_notes`、`restart_required`、`build_failed`。vm-design §13.3)。どの kind も、書く時点で `-o` の出力はその build で書き終えている。`restart_required` ではホストは出力を読み直して VM を起動し直し、driver は以後の差分をその build から作る。本文はどれも改行終端のテキスト行
- reload の適用は 1 frame を超えるブロックになり得る (作り直しは heap の大きさに比例する)。dev 専用のヒッチとして許容する

## 10. backend に依らない規則

1. reload の契約 (§5 の分類・カバレッジ) は **IL モデル (class / field / instance) の語彙で定義**し、VM の表現 (register、slot、関数表) は実装の詳細に留める
2. 計画 (`ReloadRules`、`ReloadPlan`) は backend 非依存の migration 計画を出す。VM の `tvm_apply` はその一消費者であり、計画に VM の知識を持ち込まない
3. 意味論の正本は il-spec + digest gate に置き続け、実装の挙動を仕様化せずに頼る箇所を増やさない
4. runtime の C の実装は `runtime/` に置き、VM と C が同じものを使う。片側にしかない意味論を作らない

## 11. restart の規則表と、本設計との差

restart の規則は `ReloadRules` (`SelfHost/Tool/ReloadRules.cs`。IlExport の metadata) の 1 つで、`tcs vm --watch`、`tcs vm --reload-from`、web の増分 session が同じものを使う。

| 編集 | 分類 |
|---|---|
| 型の削除・種類の変更 (class / record / struct / enum) | restart |
| base class の変更 | restart |
| 既存 static の initializer の削除 | restart |
| 既存 static (const を含む) の initializer と型の変更、const と static の入れ替え | live (その static と、それを initializer で読む static を評価し直す。`ReloadRules.ReinitStatics`) |
| enum member の削除・値の変更 | restart |
| enum member・型の追加、static の追加と削除 | live |
| instance field / auto-property の追加・削除・型・initializer の変更、record の positional member の変更 | live (instance field の initializer の変更は `reload_notes` で知らせる) |

const と static の入れ替えは、initializer の hash が const と static で別になるので、initializer の変更と同じく評価し直す。

initializer の比較は書き方に依らない形で行う (`IlExport.InitializerHash`): 整数・bool・string・null の定数は値と型で、それ以外は trivia (空白・comment) を除いた token 列で比べる。`1+2`、`1 + 2`、`3` は同じ initializer になる。float の定数は token で比べるので、`1f / 3f` と `0.333333343f` は別の initializer になる。`new()` と `new List<int>()` のように token が違う書き換えは、意味が同じでも評価し直す。

§5.2 の設計との差と、既知の制限:

- `[RenamedFrom]` (field の改名で値を引き継ぐ注釈) は IlExport に無い。改名は削除と追加になる
- 型の削除は生きている instance を見ずに常に restart (生きている instance がゼロなら live にできる余地がある)
- 既知の制限: struct の型の static を持ち、その struct を heap の object が field や要素に持たない program で struct の layout を変えると、計画がその struct を組み直しの対象にしない。field の名前が変わっただけなら値が位置で写り (足した field に消した field の値が入る)、葉の数や種類が変わると `tvm_apply` が `reload plan does not match the new version` の `TVM_LOAD_ERROR` で当てない (状態は旧版のまま)。同じ struct を heap の object も持つ program では正しく組み直す
- release (C) は reload を持たない (L0)。§3 の L1 / L2 / S は無い

## 12. 決定が必要な点

- D6: release reload のレベル選択 (§3 — L0 / L1 / L2 / S の採否と時期。推奨: 1st priority は L0 で成立させ、L1 の出力契約 (動的 module 化 + statics export) だけ C backend の設計に織り込む)
