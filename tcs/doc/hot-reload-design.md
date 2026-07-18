# hot reload 設計 — ユースケースと状態移行の統一契約

il-design §6 (eager migration) と incremental-module-compilation-design §11
(instance shape 変更 = restart) の矛盾を解消し、reload を単一契約に統一する。
本文書が reload の正本で、§6 / §11 は本文書に従う。

## 1. ユースケースと受入条件

tcs は dev (Lua) / release (tcs2c→C) の 2 backend 構成。**1st priority は
platform × mode の 2×2 = Windows dev / Windows release / web dev /
web release**。実機 (Playdate 級) は 2nd priority で、KPI floor
(perf/README) は性能下限としてのみ効かせる。

hot reload の目的はゲーム開発の反復 (挙動調整・数値チューニング・データ形状の
試行錯誤) をシーンを作り直さずに回すこと。dev backend の reload (§4-§9) と、
release backend の reload (§3 — 技術オプションを提示、採否は D6) は機構が
異なるため分けて扱う。

| UC | 環境 | ループ | 受入条件 |
|---|---|---|---|
| UC1: Windows dev | Windows 上の C ホスト (lub 等) + 組込み Lua VM | Rider で C# 編集 → driver (watch) が transpile + diff → 実行中 VM へ chunk 適用 | method 編集が次 frame から見える。field 追加/削除でシーン状態 (instance の生存値) が保持される。適用は frame 境界で、失敗時は旧状態のまま続行。restart 判定時は理由を表示してシーン再起動導線へ |
| UC2: web dev | browser WASM (Roslyn incremental) + module registry (§11) | エディタ編集 → incremental compile → applyBatch | §11 の SLO (500ms) と transaction 契約を維持。live/restart の分類は本文書 §5 に従う |
| UC3: Windows release | tcs2c → C → ネイティブ静的リンク | reload の採否は D6 (§3 のオプション) | L1 以上を採る場合は §3 の該当受入条件。L0 なら reload なし |
| UC4: web release | tcs2c → C → wasm (emscripten 系 toolchain) | 同上 (D6) | 同上。C 出力が wasm target でビルド可能であることは release 側の課題で本設計の対象外 |
| (2nd) 実機 dev | Playdate 級実機 | PC で編集 → 転送 → 実機で適用 | dev interp が 10ms 予算に収まる範囲は UC1 の延長 (chunk 自己完結性が条件)。収まらないゲームでは release reload (§3 L1) が実機イテレーションの前提になる — D6 の判断材料 |

restart が正常系である理由 (§4) も UC で決まる: dev ループで restart は
「シーンをもう一度作る」コストを意味するので、live にできる編集はなるべく
live に倒したい。ただし移行の正しさを犠牲にはしない — 移行が証明できない
編集は検出して restart にし、理由を表示する。

## 2. dev 機構の中立性契約

dev backend の reload 機構が backend 間の意味論一致 (digest gate) を壊さない
ための契約。release 自体の reload (§3) とは独立に成立させる:

- **dev 用機構 (weak registry / reload chunk / hook 呼び出し) を tcs2c は
  emit しない**。release の reload は §3 の機構で別途実現する (採る場合)
- **registry 登録は observable semantics に影響しない** (weak table への
  書き込みのみ)。dev+registry 込みで digest gate が通ることを恒常ゲートで
  保証する (現状 green)
- **`OnReload` hook は「reload を持つ実行系」でのみ発火する**。L0 release
  では dead code。ゲームロジックを hook に置くと実行系間で挙動が割れるため、
  用途は「reload 起因の derived data 再構築」に限る旨をドキュメント化する
- **reload で作られた状態はセッション限り**。cold start との互換 (セーブ
  データ等) は migration の要求に含めない
- **性能**: registry のコストは dev のみ。dev 性能の KPI floor は実機級
  10ms/frame (perf/README) に置いているため、構築頻度の高い workload での
  registry overhead を perf harness で実測する (未測 — 現 kernel は class
  構築を含まない。spawn_churn の class 版変種を追加して測る)。floor を
  圧迫する場合は transpile option 化 (dev 既定 on) を検討

## 3. release backend の reload — 技術オプション (D6)

release (AOT C) の reload は「するかどうか」ではなく「どのレベルを採るか」の
選択。レベルは独立に採否でき、S は直交。

### L0: reload なし

編集は常に再ビルド + 再起動。追加コストゼロ。

### L1: whole-module code swap (dll / wasm instance 差し替え)

- **対象編集**: method body 系のみ。layout 変更 (§5.2 で restart になる類 +
  field 追加/削除) は restart
- **機構**: script 全体を 1 つの動的 module (Windows: dll、web: wasm
  instance) にする。状態 heap は host 側 allocator が所有し swap を跨いで
  生存。statics は swap 時に metadata diff で旧→新へ値コピー (dev の
  retained 規則と同じ)。**旧 module は unload しない** — 状態内に残る関数
  ポインタ (捕捉済み closure / method group thunk) は旧 code を指し続けて
  安全に動く。§11 の「capture 済み function 値は古いまま、次の lookup から
  新 code」規則が自然に成立する
- **前提**: tcs2c 出力の動的 module ビルド (静的 link 用 `--lib` は実装済み
  — 動的化は entry 関数表 + statics の export 列の追加)、type_id の版間
  安定化 (metadata からの安定採番 — 現行 DFS 範囲 `is` と要調整)
- **コスト**: 定常オーバーヘッドほぼゼロ (module 内は直接呼び出しのまま)。
  メモリは旧 module 保持ぶんセッション内で線形に増える (GOAL と同じ割り切り)
- **受入条件**: method 編集が次 frame から見える。restart 境界は dev より
  広い (shape 系は全部 restart) が、判定と理由表示は同じ ReloadPlanner

### L2: L1 + shape migration (CLOS on C)

- **追加機構**: allocation registry (割付時に type_id 付きで記録)、per-type
  pointer map (tcs2c は全 field 型を知っているので codegen 可能)、layout
  変更時の全 object 再構築 + pointer 書換 (moving GC 相当の精密メタデータ)、
  type_id 再タグ付け (enumerate して header 書換)
- **コスト**: 大。割付経路に常時 bookkeeping が入り release 性能に影響
  (採るなら perf harness で実測必須)。実装規模も最大
- dev と同じ編集分類 (§5.2) を release でも成立させる唯一のレベル

### S (直交): snapshot / restore による高速 restart

- 状態を直列化して保存 → 新 binary 起動 → 復元。live ではないが shape 変更も
  一様に扱え、実装は L2 より遥かに小さい (直列化メタデータは layout 情報が
  既にある)。L1 と組み合わせると「method 編集 = live、shape 編集 = snapshot
  restart」の二段になり、体感は dev reload に近づく
- web release の更新 (live-ops 的な差し替え) にも流用できる

### 判断材料 (D6)

- Windows dev / web dev では dev backend の reload で開発ループが成立する
  ため、1st priority の範囲では release は L0 でも開発体験は欠けない
- release reload が効くのは: (a) release でしか性能が出ない対象での
  iteration (実機級 — 2nd priority だが §1 の通り実機 dev の前提になり得る)、
  (b) 長い再現手順を要する終盤チューニングを release build で行う場合、
  (c) web live-ops 的な更新 (S で足りる可能性が高い)
- 推奨順: まず L0 で 1st priority を成立させ、L1 は「dll/wasm module 化 +
  statics export」という前提部分だけ tcs2c の設計に織り込んでおく (後から
  L1 を足すときに出力契約を壊さないため)。L2 は実機需要が確定してから

## 4. 設計原則と §11 を覆す根拠

**すべての編集は (a) 完全な移行カバレッジを証明できる live apply か、
(b) requiresRestart のどちらかに分類される。無言の部分移行は存在しない。**

§11 は「既存 instance を安全に migrate できない」ため instance shape 変更を
restart とした。以下の 3 点でこの前提が変わったため、dev では shape 変更を
live 側へ緩和する:

1. **weak instance registry** (il-design §6、実装済み) — 生存 instance を
   構築時 class 付きで列挙できる
2. **two-phase migration** (§7) — 評価と適用を分離し、エラー時は一切
   mutate せずに abort できる (§11.4 transaction と両立)
3. **conservative restart detector** (§6) — schema 経由で到達できない
   struct 値の残存を v1 の IL から静的検出し、restart に倒せる

## 5. 状態カバレッジと編集分類 (dev)

### 5.1 状態カバレッジモデル

migration が到達できる生存状態の全域 (roots):

| root | 到達方法 | 移行 |
|---|---|---|
| class/record の static field | class table 直接 | 値保持 / diff 適用 |
| 生存 instance の field | weak registry walk | diff 適用 |
| struct 値 (instance/static の field、配列要素・任意ネスト) | owner walk + 静的型 | 再直列化 |
| struct 型 table の static field | struct table 直接 | class static と同規則 |
| 実行中 frame の local / 引数 | — | 対象外 (frame 境界契約で死んでいる) |
| closure upvalue の **instance 参照** | registry 経由で instance 自体に到達 | field diff は届く |
| closure upvalue の **struct 値** | 到達不能 | **restart detector 対象** |
| List / Dictionary 要素の struct 値 | 型付き walk 未対応 | **restart detector 対象** |
| capture 済み function 値 | 到達不能 | 移行しない (§11 と同じ documented 挙動: 次の table lookup から新 code) |

前提: yield/iterator/coroutine は TCS1001 (検証済み) のため、frame を跨いで
生存する Lua frame はホスト都合の callback closure のみ。

### 5.2 編集分類マトリクス

| 編集 | 分類 | 規則 |
|---|---|---|
| method / accessor / operator body | live | class table 上で置換 (identity 不変)。削除は key 削除 |
| instance field / auto-prop の追加 | live | initializer render (無ければ型 default、struct は zero 値) を全生存 instance へ |
| instance field の削除 | live | 全生存 instance から key 削除 |
| instance field の同名型変更 | live | 新型 default へ reset (値の再解釈はしない) |
| instance field の改名 | 注釈依存 | `[RenamedFrom]` があれば値引き継ぎ、なければ削除+追加 (=値消失、診断で警告) |
| struct layout 変更 (§6 detector 通過時) | live | owner walk で再直列化。struct in struct は再帰 |
| struct layout 変更 (detector 引っかかり) | **restart** | closure capture / List / Dict 残存の可能性 |
| static field 追加 | live | 副作用なし initializer のみ (§11 のまま)。証明不能なら restart |
| static field 削除 | live | key 削除 |
| 既存 static の initializer 変更 | **restart** | §11 のまま (値意図の変更は restart で反映) |
| 既存 static の値 | live | 保持 (再実行しない) |
| base class 変更 (再親付け) | **restart** | §11 のまま。継承 field 集合と ctor 連鎖の差分移行は v1 対象外 |
| class/record/struct/enum の追加 | live | 新規定義のみ |
| type 削除・改名 | 条件付き | registry で生存 instance ゼロを確認できれば live、あれば restart (§11 の「判定できない」を registry で判定可能に) |
| record positional member 変更 | live (提案) | class の field diff と同規則 (__eq は新 field 集合で再計算) — §11 からの緩和、要承認 (D3) |
| enum member 追加 | live | 定数テーブル再定義のみ |
| enum member の削除・renumber | **restart** | 保存済み int 値の再解釈は不能。enum member 列を metadata に追加して検出する |
| top-level statement の編集 | **restart** | §11 のまま。reload chunk は宣言のみ emit し top-level を再実行しない |
| interface | live | Lua 出力なし、型検査のみ |

## 6. conservative restart detector

struct S の layout が変わったとき、v1 プログラムの IL/semantic model を走査し
以下のいずれかに S が現れたら requiresRestart(理由付き) とする:

- lambda / closure の capture 変数の型 (S、S[]、S を含む struct)
- `List<S>` / `Dictionary<K,S>` / `Dictionary<S,V>` の型引数
- その他 schema 型付き slot (class/record field、static、配列、local、param、
  return) 以外の残存経路が増えた場合はここに追記する

検出は v1 (旧版) 側で行う — 生存状態は旧版のプログラムが作ったものだから。

## 7. two-phase migration (atomicity)

§11.4 transaction と両立させるため、instance migration は 2 段で行う:

1. **evaluate**: registry を配列へ snapshot (pairs 中の挿入禁止を回避) し、
   added field の initializer・struct 再直列化の新値をすべて side table へ
   評価する。ここでの Lua error は一切の mutate なしで abort → rollback 不要
2. **apply**: 単純代入のみ (エラー経路なし) で side table を反映する

OnReload hook は apply 完了後。hook の error は commit を巻き戻さず警告
(§11 の onReload と同じ)。

## 8. hook

| hook | 対象 | 契約 |
|---|---|---|
| `void OnReload()` (instance method) | migration された各 instance に 1 回 | field diff 適用後。順序は不定。派生で継承可 |
| entry type の `OnReload()` (§11 既存) | reload commit 後に 1 回 | derived data (mesh 等) の再構築用。instance hook の後 |

## 9. アーキテクチャとホスト統合契約

```
v1 metadata ──┐
              ├─ ReloadPlanner ── 分類 (live batch / requiresRestart + 理由)
v2 metadata ──┘        │
                       ├─ chunk backend  (HotReload.EmitReloadChunk — UC1、将来の実機 dev)
                       ├─ module backend (ModuleRegistry.applyBatch — UC2)
                       └─ (D6 で採る場合) release backend (L1: module swap 計画 / L2: migration 計画)
```

- 分類と migration 計画 (field diff、struct 再直列化計画、restart 理由) は
  ReloadPlanner に一本化し、各 backend は適用形式だけを持つ。release の
  編集分類 (L1: method body のみ live) も同じ Planner の出力から導く
- metadata は IlExport (Classes/Structs + 追加予定: record、enum member 列、
  initializerHash) を正とする。LayoutHash は変更検知の高速化用の派生値で、
  分類の根拠は metadata diff とする
- **ホスト統合契約 (UC1、将来の実機 dev)**: ホスト側の義務は「frame 間に
  受け取った chunk 文字列を 1 回実行する」のみ。v1 metadata の保持・diff
  計算・restart 判定・chunk 生成はすべて driver 側 (UC1 では同一マシンの
  watch プロセス)。restart 判定時は chunk を送らず、ホストの再起動導線
  (プロセス/シーン再起動) を使う。この自己完結性が実機 dev への延長条件
- reload の適用は 1 frame を超えるブロックになり得る (migration は
  O(生存 instance × 変更 field))。dev 専用のヒッチとして許容し、SLO は
  UC2 のみ (§11 の 500ms) に置く

## 10. Lua 依存の将来性 (粉砕可能性を殺さない)

release (tcs2c) は既に Lua 非依存 (独自 C runtime)。Lua 依存は dev 側に
集中しており、その面は次の通り:

| 依存面 | 現状 | 粉砕時の代替 |
|---|---|---|
| dev 実行系 | Lua 5.5 VM (lua32) | (A) 自前 IL interpreter、または (B) release backend + §3 L1/L2 を dev に転用 |
| dev コード生成 | Lua source emit | IL がそのまま入力になる (A)、C emit (B) |
| reload の動的性 | table/metatable/load() | (A) VM native の reload (registry/migration を VM 組込みにでき、Lua glue より単純化する)、(B) §3 L1/L2 |
| module/incremental (§11) | Lua chunk descriptor | descriptor の中身を IL/bytecode に差し替え (形式は §11 の manifest 構造のまま) |
| web dev 実行系 | Lua を wasm ビルド | (A) IL interp を wasm ビルド、(B) はブラウザ内 C toolchain が重く不利 |
| host (lub) 統合 | lub が Lua を embed | lub 側が tcs VM / dll を embed する形へ (lub はこちら所有なので長期では可動) |

判断材料:
- Lua を消す動機: backend 二重実装の解消 (機能追加が emit 2 回 → 1 回)、
  VM 都合の言語制約の除去、dev/release 性能ギャップ (38-50x) の縮小
- Lua が現に買っているもの: 成熟 VM (GC/エラー処理/wasm ビルド実績)、
  reload に必要な動的性がタダ、lub 既存統合。vendored Lua 5.5 は小さく
  supply chain リスクも実質ない
- 判断時期: 今ではない。L1 (release module swap) が成熟して dev ループを
  賄えるか、または dev-on-device が interp で 10ms に収まらないと確定した
  時点が再評価点

### デバッグ体験の評価軸 (D7 の判断材料に含める)

経路選択はデバッグのしやすさで大きく差が付く。現状の source map は
**行レベル**で、lowering (IIFE / temp 変数 / 補間→format / 文展開) が
式レベルの対応を崩す — 「C# の 1 行 → Lua 複数行」で breakpoint 意味論が
濁る、`__tcs_*` temp が変数ペインに漏れる、といった構造的な限界がある。

| 経路 | エラー trace | breakpoint / step / 変数 inspect | 備考 |
|---|---|---|---|
| dev = Lua (現行) | source map で C# 行へ復元可 (最低限の bar) | Lua debug hook + source map 越しの DAP を自作すれば可。ただし lowering 起因の step 挙動の濁り・C# 式での watch 不可は残る | 二重の頭 (書くのは C#、動くのは Lua) が常時かかる |
| dev = 自前 IL interp (§10 A) | IL は Origin (構文 span) を保持済みで 1:1 | VM native の debugger にできる — breakpoint/step/locals が C# 語彙に直結。DAP 自作コストはあるが Lua 経由の写像より単純 | **debuggability は A の最大の利点** (§10 の表で過小評価しない) |
| dev = release 転用 (§10 B / §3 L1) | C に `#line` を emit すれば native debugger (VS/lldb) が直接 .cs へ写像 | VS でそのまま step 可。-O2 の並べ替えと dll swap 後の breakpoint 再解決が濁り所 | Windows は強い。wasm は DWARF 対応がツール依存で粗い |
| web dev (Lua wasm) | 〃 (trace 復元のみ) | wasm 内の Lua interp の二重間接でネイティブには不可。IL interp を wasm 化すれば page 内 debugger を自前で出せる | 現状の実質解は trace + print |

reload 固有の要求: N 回 reload 後の stack trace は「どの版の source か」を
含めて復元する必要がある。§11 は revision 付き chunk name + source map
index で既に解決しており、chunk backend (UC1) の driver も同じ方式を採る。

**選択肢を殺さないための規則 (今から効かせる)**:
1. reload の契約 (§5 の分類・カバレッジ) は **IL モデル (class/field/
   instance) の語彙で定義**し、Lua の table/metatable は chunk backend の
   実装詳細に留める — 本文書の §5 は既にこの形
2. ReloadPlanner (§9) は backend 非依存の migration 計画を出す。Lua chunk
   emission はその一消費者であり、Planner に Lua 知識を持ち込まない
3. 意味論の正本は il-spec + digest gate に置き続け、Lua 挙動を仕様化せずに
   頼る箇所を増やさない (既存方針の維持)
4. runtime/ の Lua helper は tcs2c CRuntime と対で保ち、片側にしかない
   意味論を作らない

## 11. 現実装 (T220a-c) と本設計の差分

現実装は chunk backend の先行実装で、以下が本設計に未達 (実装 GO 後に解消):

- ReloadPlanner 未分離 (分類なし — すべて live 扱い) / restart detector なし
- top-level statement を v2 chunk が再実行してしまう (§5.2 違反)
- record class が migration 対象外 (旧 instance と新定義の split-brain)
- instance migration が single-pass in-place (§7 違反: pairs 中の instance
  生成 initializer でエラー・未定義動作の余地、エラー時の部分適用)
- 既存 static initializer 変更を検出しない (§5.2 の restart 条件を素通し)
- struct table の static merge なし (struct static 値が reload で消える)
- enum metadata / `[RenamedFrom]` / initializerHash が IlExport にない
- registry overhead の実測なし (§2 の class 構築 workload が perf harness に
  未収載)
- namespace は現 emitter がフラット global に emit するためそのまま動くが、
  emitter が実 namespace 対応した時点で alias 解決 (§11 の global alias) に
  追従する必要がある

## 12. 決定が必要な点

- D1: §11 の restart 境界を §5.2 の通り緩和する (instance shape live 化) か
- D2: restart 維持項目 (base 変更 / static initializer 変更 / enum renumber)
  の確認
- D3: record positional member 変更を live (class 同規則) にするか §11 の
  restart のままにするか
- D4: instance `OnReload()` + entry `OnReload()` の 2 段 hook 構成と命名
- D5: 実装を §11 の差分解消へ進める GO (別途、計画提示のうえ)
- D6: release reload のレベル選択 (§3 — L0 / L1 / L2 / S の採否と時期。
  推奨: 1st priority は L0 で成立させ、L1 の出力契約 (動的 module 化 +
  statics export) だけ tcs2c 設計に織り込む)
- D7: Lua 依存の扱い (§10 — 当面は維持しつつ「殺さない規則」1-4 を採用する
  か。粉砕の再評価点は L1 成熟時 or dev-on-device が floor に収まらないと
  確定した時)
