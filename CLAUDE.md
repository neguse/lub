# CLAUDE.md

lub は code-centric な game 制作 runtime。C# (TinyC#) → Lua transpile + hot reload で
game を書き、native (DX12 / Vulkan 直接実装 + SDL3 GPU) と web (WASM + WebGPU)
の両方で動かす。

## 現状の source of truth

memory に状態を溜めない。現在地は常に以下を読む:

- `docs/roadmap.md` — Phase の定義と進捗
- `docs/design.md` — 設計方針
- `docs/api-glue.md` — 多言語 (Lua/C#) binding の構成と実装ライブラリの供給方針
- `docs/serve.md` — `lub --serve`(HTTP + SSE で web ホットリロード)。外部リポからゲームを書くテンプレートは `templates/game/`(C# の csproj。tcs→Lua と .NET 実行)
- `git log` — 実装経緯

## 流儀

- PR ベースで回す。作業はブランチ → PR。マージ gate は PR CI(linux / windows / web の 3 workflow)で、deploy は master push 時に web workflow が同じ verify を通した上で行う。push は必ずユーザー承認後、merge は人間の act。
- テストの走り分け: commit フック = format + 空白 + docs lint(秒)。フル検証は PR CI が担う(linux: `scripts/native-gate.sh` = docs lint/build/smoke/物理 Lua/golden/C# gate、windows: build + WARP golden、web: build + headless verify + web golden)。手元でフル検証したいときだけ `scripts/pre-push.sh`(CI と同内容の手動ゲート)。
- 移植は理想設計で。原典 (NGS 等) のコード構造・ファイル分割・抽象化は真似しない。C# + lub 哲学から導いた最良案で書く。忠実に写すのは gameplay rule (敵パターン・弾数・HP・速度・出現タイミング) だけ。ファイル分割は概念単位 (Scene, EntityWorld, Atlas, Font, DrawList)、state machine は interface/enum/class、entity は List/Pool。
- ドキュメントは `docs/README.md` の方針に従う。live/record のディレクトリ分離、平易な語(jargon・完了注記・bold 禁止)、記録本文は当時のまま。機械検査は docs-lint。
- フォーマッタはツール標準デフォルト。既存スタイルに寄せる設定ファイル (`.clang-format` / `.prettierrc`) は置かない。clang-format=LLVM default、dotnet format=default、stylua=default、prettier=default。整形は `scripts/format.sh`(`--check` で CI)。

## サンプル構成

各サンプルは `samples/<name>/{<Entry>.cs, <Entry>.csproj, data/...}` に自己完結。

- csproj は basename = entry class(`Hello00.csproj` → `Hello00`)
- ソースはデータを `Io.LoadText("samples/<name>/data/...")` と cwd 基準で参照
- 共有 Lua (`boot.lua`、生成した `lubx.lua`) は `samples/` 直下
- 生成 Lua は `samples/<name>/.lub/<Entry>.lua`(`.lub/` は gitignore)
- C 側 bare-name 解決 (`src/main.c`) と web playground (`web/playground/samples.ts`, `verify-headless.mjs`) も対応済
- C# (TinyC#) が authoring 言語(サンプル一覧の正は `web/playground/samples.ts` の CS_SAMPLES)。実行は `lub samples/<name>/<Entry>.csproj`(transpile + watch + hotswap、要 dotnet SDK + `third_party/tcs` submodule)。csproj は basename = entry class の規約で、lub は MSBuild 評価をしない(IDE 型チェック用の実ファイル)。check/build のみは `scripts/run-cs-sample.sh <name> --check|--build`。共有 stub は `cs-lib/lub_stub.cs`(root class `Lub` の下に `Gfx` / `Input` / ... と enum。ゲームは `using static Lub;` で `Gfx.BeginPass(...)`)。C# は通常の命名(PascalCase)で書き、tcs が Lua の snake_case(`lub.gfx.begin_pass`)に写す。API 面は生成 binding が作る `lub` table。stub の検査と生成物は `tools/lub-gen`(`docs/api-glue.md`)。web は playground(`#sample=<name>`)
- .NET 実行: 同じ C# ソースを実 .NET で動かす経路。`dotnet/Lub`(生成した facade + host、`Lub.Run(typeof(Game), args)`)が共有 library(`build-release-linux/liblub.so`、CMake の `lub_shared`)を P/Invoke する。サンプルは `dotnet run --project dotnet/SampleRunner -p:Sample=<name> -- --capture out.png`(要 `LUB_NATIVE_LIB` か出力隣の共有 library)。native gate が tcs→Lua と .NET の frame digest(`--digest`、C API 呼び出しの構造の hash)を比較する。
- raw Lua のサンプルは `samples/<name>/<name>.lua`(on_init / on_frame を持つ table を返す module。`lub samples/<name>/<name>.lua` で起動)。lubx は `samples/lubx.lua`(cs-lib から `scripts/gen-lubx-lua.sh` が生成する checkin 済みの Lua、`local lubx = require("lubx")`)経由で使う。cs-lib を変えたら再生成する(native gate が `--check`)。書き方は `docs/manual/09-raw-lua.md`
- 実装モジュール(lub.Math, lubx)は `cs-lib/` にあり、サンプルの一部という位置付け(runtime は Lua を供給しない)。多言語 binding の構成は `docs/api-glue.md`
- API の記述は `cs-lib/lub_stub.cs`。C API の header(`include/lub/lub_api.h`)、Lua binding(`src/gen/lua_api_gen.c`)、surface test、API docs のデータ(`web/gen/lub-api-docs.json`)、.NET 実行の facade(`dotnet/Lub/Lub.g.cs`)は生成物で手で編集しない。stub を変えたら `scripts/gen-api.sh` で再生成する(native gate が `--check` で差分を検査)。Lua の面は生成 binding が作る `lub` table だけ(prelude は無い)

## web / WASM verify

- emcc は PATH に無いが存在する: `source ~/emsdk/emsdk_env.sh` で使える。`which emcc` だけで「無い」と早合点しない。
- build: `emcmake cmake --preset wasm-release` → `cmake --build build/wasm -j`。既存 build/wasm が Unix Makefiles だと preset(Ninja)の configure は mismatch で失敗するが、`cmake --build build/wasm` は既存設定で再ビルドできる。
- verify: `cd web && npm run dev`(localhost:5173)起動 → 別プロセスで `LUB_URL=http://localhost:5173/ npm run verify`(playwright + chromium swiftshader)。A1-A4=初期描画/hot reload、A5=全サンプル切替、A6=C# 増分編集が commit ACK まで貫通、A7=診断のエディタ内表示と生成 Lua タブ、A8=C# 補完/hover(レイテンシ観測ログ付き)。
- web golden: 同じ dev server に対して `LUB_URL=... npm run golden`。native golden と同じ curation(20 サンプル、frame 30/120/240、fixed-dt)を wasm の --capture 経路(backend_webgpu の wg_capture)で撮り、`tests/golden/<name>_web.png` と byte 比較。golden は swiftshader 固有なので playwright/chromium を上げたら `npm run golden -- --update` で再生成。
- playground の C# は増分 session(tcs `SessionExports`、設計は tcs `doc/incremental-module-compilation-design.md`): 編集は 75ms debounce → 変更 .cs のみ Update → LinkSnapshot(registry apply する単一 entry Lua)→ hotswap → runtime の `@@tcs_commit` ACK で「synced rev N」表示。warm body edit は p95 0.45s 級、restart 分類(static initializer / shape / base 変更等)は fresh player 起動。E2E 測定は tcs `bench/chrome-e2e-ack.mjs`(lub の web/ から実行)。
- tcs / WasmCompiler を変えたら `cd web && npm run gen-tcs -- --publish`: playground の C# コンパイラは `web/tcs-wasm-assets/`(gitignore)に固めた .NET wasm bundle。生成には dotnet SDK + wasm-tools workload が要る。cs-lib / C# サンプルを変えたら `npm run gen-tcs-prebuilt` も(prebuilt snapshot = `web/tcs-prebuilt/`、cold 起動 0.5s の正体。古いままだと初回表示だけ旧コードになる — 編集すれば直る)
- A5 は nonBlack 判定が甘く、コンパイル失敗しても前サンプルの絵で PASS しうる(playerReady timeout 警告が出たら疑う)。
- verify が「compiling…」のままハングしたら vite dev server の詰まりを疑って再起動(長時間稼働 + 大量ファイル変更で worker モジュール変換が無音で止まることがある)。
- lub.js 構文チェック: `node --check build/wasm/lub.js`。
- docs サイト (`/docs.html`): ガイドは `docs/manual/*.md`、API reference は `cs-lib/lub_stub.cs` の XML doc が single source of truth。`scripts/gen-api.sh` が `web/gen/lub-api-docs.json` に固め、`web/scripts/gen-api-docs.mjs`(`npm run gen-api`、dev/build に組み込み済)が markdown を HTML にして `web/public/api-docs.json`(gitignore)を作り、`web/playground/docs.ts` が描画する。
