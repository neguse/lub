# hot reload の経路の統合 — native の watch と web を 1 つの session と registry にする

> Status: 設計中

native の watch (`--watch --reload-chunks`、1 本の出力と reload chunk) と、web の増分 session (`IncrementalCompilationSession`、module の snapshot と `ModuleRegistry`) を、1 つの経路にする。
compile 側は session、実行側は registry に揃え、reload chunk の経路は最後に消す。

## 1. 方針

- 判断の基準は「今の 1 回が速いか」ではなく「経路が 1 つになり、この先の高速化と機能がどちらの host にも効くか」。
- 実行側は registry に揃える。registry は transaction で失敗を巻き戻し、snapshot は起動にも live の適用にも同じものが使え (idempotent)、hash の同じ module は当てない。適用の手間が変わった module の数で決まるので、増分の compile と釣り合う。reload chunk は毎回全部の型を定義し直す。
- 意味は、今の 2 つの経路の強い方に揃える。watch だけが持つ instance の移行 (field の追加・削除・型の変更、struct の値の組み直し、instance ごとの `OnReload`) は registry に移す。これを落とすと、今 live で当たる field の追加が restart になり、watch の利用者には後退になる。
- host (lub の native、`lub --serve`、web の player) は、どれも同じ snapshot を同じ手順で当てる。

## 2. 今の 2 つの経路の差

| 項目 | watch (reload chunk) | session と registry |
|---|---|---|
| compile | 保存のたびに全 file を変換 | 常駐の session。body だけの編集は method の差し替え |
| 当て方 | chunk を `_G` で実行。失敗したら VM を作り直す | `applyBatch`。失敗は巻き戻して前の版のまま |
| 型 table | `_G` の global | registry の `types`。module は read-only の env で定義し、`_G` には無い |
| instance | `__tcs_instances` を辿って移行 | 移行しない。instance の形が変わると restart |
| struct の layout の変更 | 値を組み直す | restart |
| `OnReload` | instance ごとに `inst:OnReload()`、その後 host が entry と overlay に | registry が entry の static `OnReload()` を 1 回 |
| restart の判定 | `ReloadRules` (`migratesInstances: true`)、VM の動いている build と比べる | 同じ規則 (`migratesInstances: false`)、session の前の revision と比べる |
| `--trace` | あり | なし。trace の global の代入は env が拒む |
| 版の guard | `__tcs_build` の from → to の assert | revision の単調増加。古い版は黙って飛ばす |
| source map、`--prelude`、`--no-runtime`、`--module` | あり | なし |
| 複数の file の同時の保存 | 1 回の変換 | file ごとに `Update` (revision も file ごと) |

`doc/incremental-module-compilation-design.md` §16 の「native は static を再実行する」は今は当たらない (chunk も static を保つ)。今の差は上の表の instance、struct、`OnReload`、`--trace`。

## 3. 目標の形

```mermaid
flowchart LR
  E[保存] --> S[IncrementalCompilationSession]
  S -->|module の artifact| L[ModuleLinker]
  L -->|snapshot| O[-o の出力]
  L -->|snapshot の frame| H[host]
  O -->|起動・restart| H
  H -->|applyBatch| R[ModuleRegistry]
  R -->|instance の移行と OnReload| V[VM]
```

- watch は session を常駐させ、debounce の間に保存された file をまとめて 1 つの revision にする。
- 成功した revision ごとに、snapshot を `-o` に書いてから frame で送る。host は frame の snapshot を当てる (mtime は見ない)。restart は `-o` の snapshot から VM を起こす。
- restart の判定は、VM が動いている revision (host が当てた最後の snapshot) と比べる。
- compile error は `build_failed`、restart は `restart_required` のまま。

## 4. 段階

各段は、その段だけで今の利用者を壊さずに入る。最後の段まで、reload chunk の経路は今のまま動く。

### 4.1 session の emit を watch と同じにする

- `EmitModule` と method の差し替えに、instance registry と `--trace` を渡す。registry の env は trace の global (`__tcs_trace`、`__tcs_trace_classes`) を host に通す。
- top-level 文、`--entry` の検査、`--module`、source map、`--prelude`、`--no-runtime` を、snapshot でも使えるようにするか、使えないときは watch が今と同じ error で止める。
- 受入: session と全体の変換の実行時の差分 test (同じ program を両方で動かして結果が一致)。今の `IncrementalDifferentialTests` は全体の変換を自分と比べているだけなので、ここで本物にする。

### 4.2 registry に instance の移行を入れる

- `applyBatch` の commit の後に、旧い形と新しい形 (field と型、initializer、struct の layout) から instance と struct の値を移し、instance ごとに `OnReload` を呼ぶ。規則は `HotReload` と同じで、`migratesInstances: true` に揃える。
- module の artifact に、移行に要る形 (field の型の列、足した field の initializer) を足す。`IlExport.ExportShape` と同じものを session の model から取る。
- 受入: reload fuzz の不変量 (retained の保持、added は initializer、dropped は nil、identity、method の差し替え、`OnReload` 1 回) を registry の経路で通す。`ReloadRulesAlignmentTests` の意図した差は無くなる。

### 4.3 watch を session で動かす

- 保存された file の集合を 1 回の `Update` (複数 file) に渡し、1 つの revision にする。`--ref` が変わったら session を作り直す。
- frame に snapshot の kind を足し、`-o` を書いてから送る。restart の判定は VM の動いている revision と比べる。
- 速さ: body だけの編集は session の fast path、それ以外は session の slow path。slow path にも、`doc/watch-rebuild-design.md` §4.1 の model の共有を入れる。
- 受入: Watch* の test 一式を snapshot の frame で通す。linux-arm64 の実機で、body の編集と宣言の編集の保存 → 適用を測る。

### 4.4 host を snapshot に移す (lub)

- native: snapshot の frame を chunk と同じ時点 (frame の頭、`OnFrame` の前) で実行し、registry が当てる。失敗は前の版のまま `OnBuildFailed` と同じ形で知らせる (VM は作り直さない)。`__tcs_module_runtime` を見て、mtime の hotswap を止める。
- overlay の型は `lua_getglobal` ではなく registry から引く。Rewind の根は registry の `types` (overlay の型を除く)。`Sys.TakeTrace` と `Gfx.Pick` は 4.1 の trace のまま動く。
- `OnReload`: registry が instance ごとと entry の分を呼び、host は overlay の分だけを呼ぶ (二重に呼ばない)。
- `lub --serve` と web の player も、同じ snapshot を同じ入口で当てる。
- 受入: lub の C# サンプルと、lub を使う host の runtime test 一式が通る。

### 4.5 reload chunk の経路を消す

- `--reload-chunks` の chunk、`HotReload` の chunk の組み立て、`__tcs_build` の guard、host の chunk の入口を消す。C backend の `IlExport` は残す。
- 受入: watch と web の hot reload の test が registry の経路だけで通る。

## 5. 決めること

- 4.2 の `OnReload` の呼び方 (registry と host のどちらが、どの順で呼ぶか)。案は 4.4 のとおり。
- snapshot の frame の中身を、毎回全部の snapshot にするか、変わった module だけの batch にするか。案は全部 (idempotent で、起動と live が同じ。大きさは約 40 file で約 300 KB。native の Lua 5.5 の load は 156 KB で 5.0 ms、471 KB で 16.7 ms だった (done.md) ので、約 10 ms と見込む。測ってから決め直す)。
- 型 table を `_G` に置かないことで、host や user の Lua が global の class を引けなくなる。lub の overlay と Rewind は 4.4 で直す。ほかに global を前提にしているものを 4.4 の前に洗い出す。
