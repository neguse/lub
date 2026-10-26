# watch の rebuild 設計 — 保存 1 回で要る仕事だけをする

> Status: §4.1〜§4.3 は reload chunk の経路 (`--watch --reload-chunks`) に入れ、§4.4 で watch を session と registry の経路 (`--watch --reload`、`doc/unified-reload-design.md`) に移した。reload chunk の経路は消した。§4.1 の model の共有は session にも入れた

`--watch --reload-chunks` の保存 1 回 (保存 → `@@tcs_reload_chunk`) を、その保存に本当に要る仕事だけで組み直す。
状態の移行の規則 (`doc/hot-reload-design.md`) と、browser の増分 session (`doc/incremental-module-compilation-design.md`) は変えない。

## 1. 結論

- 保存 1 回で、全 file の method の本文を Roslyn が約 8 回 bind している。要るのは、diagnostics の 1 回と、検査と emit が共有する 1 回。
- rebuild ごとに compilation を 1 つにし、tree ごとに 1 つの SemanticModel を、検査・emit・chunk で共有する。
- restart の判定は宣言 (型の形) だけで行い、method の本文の IL は作らない。
- chunk の v2 定義は、出力と同じ 1 回の emit から取る。
- body だけの編集は、変わった method だけを emit して差し替える chunk にする (増分 session の fast path の判定を使う)。
- native の並列化は、上の重複を消した後に測ってから決める。

## 2. 実測

linux-arm64 の実機 (8 core)、約 40 file (約 1000 行の file を含む) の project、method の本文の 1 行を変える保存。

| build | 保存 → chunk |
|---|---|
| NativeAOT | 2.27〜2.48 秒 |
| JIT (温まった後) | 1.93〜2.32 秒 |

JIT の build を `perf record -g` で sampling した、保存 1 回あたりの CPU 時間 (約 3.5 秒。2 本の thread で並べて走る)。

| 経路 | 段 | CPU | うち Roslyn の bind |
|---|---|---|---|
| 出力の変換 | `UsesLibrary` | 216 ms | 170 |
| | diagnostics | 231 ms | 194 |
| | compliance | 438 ms | 207 |
| | emit | 460 ms | 227 |
| plan の IL | `UsesLibrary` | 222 ms | 175 |
| | class の IL (本文を含む) | 417 ms | 235 |
| | foreign の IL | 198 ms | 130 |
| plan の chunk | `UsesLibrary` | 229 ms | 177 |
| | emit | 486 ms | 249 |
| 別 thread | JIT の tiering と GC | 約 400 ms | — |

重複の出所:

- compilation が 3 つある (出力の変換、`IlExport.Export`、`HotReload.EmitChunk` の v2 の変換)。
- 1 つの compilation の中でも、diagnostics (`compilation.GetDiagnostics`)、`UsesLibrary`、compliance、emit が、それぞれ別の SemanticModel で本文を bind する (`compilation.GetSemanticModel` は呼ぶたびに新しい model を返し、bind の結果は model ごとにしか残らない)。
- `UsesLibrary` は、library を使わない project では全 file の全部の名前に `GetSymbolInfo` をかける。
- chunk の v2 定義は、出力とほぼ同じ Lua (違いは `cacheTypeLocals` だけ) を bind からやり直して作る。
- plan の IL は method の本文まで IL にするが、reload が使うのは型の形、method の名前、field の initializer の IL と hash だけ。

## 3. 保存 1 回で要る仕事

| 仕事 | body だけの編集 | 宣言が変わる編集 |
|---|---|---|
| parse | 変わった file | 変わった file |
| 型と準拠の検査 (bind) | 変わった body | 全 file を 1 回 |
| Lua の生成 | 変わった method | 全 file を 1 回 |
| restart の判定 | 要らない (型の形が変わらない) | 宣言だけを比べる |
| chunk | method の差し替え | 1 回の生成の結果と移行の処理 |

## 4. 設計

### 4.1 bind を 1 回にする (全編集)

- rebuild は compilation を 1 つ作り、tree ごとに SemanticModel を 1 つ作って、`UsesLibrary`、compliance、static の順、emit に渡す (`SemanticModelCache`)。
- diagnostics は `compilation.GetDiagnostics` のまま。その bind は model の cache と別で、tree ごとの model の `GetMethodBodyDiagnostics` に替えても後の bind は減らなかった。
- `UsesLibrary` は残すが、共有した model の上で引くので bind はほぼしない。

### 4.2 restart の判定を宣言だけで行う (全編集)

- `ReloadClassifier` と `HotReload` が使う形 (class・struct・enum、field と型、layout hash、static の initializer の hash、method の名前、足した field の initializer の IL) だけを、4.1 の model から取る。
- method の本文の IL、foreign の IL は、reload では作らない。C backend の `IlExport.Export` は変えない。

### 4.3 emit を 1 回にする (全編集)

- watch の `--reload-chunks` は、出力も `cacheTypeLocals: false` で emit し、その Lua を chunk の v2 定義にも使う。
- 出力の Lua は、型 table を chunk-local に cache しない分だけ遅くなる。dev の watch だけの変更で、1 回の変換と `--snapshot` は変えない。

### 4.4 body だけの編集を差し替えにする

- watch を session と registry の経路に移して行う (`doc/unified-reload-design.md`)。reload chunk の経路の中に別の fast path は作らない。

### 4.5 並列化 (native のみ)

- 4.1〜4.4 の後に同じ benchmark で測り、まだ足りなければ、tree ごとの compliance と bind を並べることと、`concurrentBuild` を検討する (WASM は 1 thread のまま)。

## 5. 段階と受入条件

各段で、2 章と同じ project と保存を同じ機器で測り、段ごとの保存 → chunk と CPU の内訳を done.md に書く。

| 段 | 受入条件 |
|---|---|
| 4.1 | 出力の Lua と diagnostics が変更前と一致する。`bash run-tests.sh` が通る |
| 4.2 | restart の判定と chunk が変更前と一致する (`HotReloadTests`、`HotReloadRestartTests`、`WatchRestartTests`、reload fuzz) |
| 4.3 | chunk を当てた VM の振る舞いが変更前と一致する (watch の test 一式) |
| 4.4 | `doc/unified-reload-design.md` の各段の受入条件 |

目標 (案): 宣言が変わる編集で保存 → chunk 1 秒以下、body だけの編集で 0.2 秒以下 (linux-arm64 の NativeAOT)。

## 6. 未確認

- `HotReload` が method の本文の IL と foreign の IL を使っていないこと (コードを読んだ範囲では、使うのは method の名前と field の initializer の IL だけ)。
- NativeAOT の build での内訳 (2 章の内訳は JIT の build)。
