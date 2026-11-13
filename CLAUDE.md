# CLAUDE.md

lub はコード中心のゲームエンジン。C# (TinyC#) で game を書き、tcs VM の bytecode にして hot reload で動かす。
native (DX12 / Vulkan / Metal 直接実装 + SDL3 GPU) と web (WASM + WebGPU) の両方で動く。

## 現状の source of truth

memory に状態を溜めない。現在地は常に以下を読む:

- GitHub の issue — これからやること。やったことは PR の本文に書く(リポの中に作業の一覧や完了の記録は置かない)
- `docs/design.md` — 設計方針
- `docs/api-glue.md` — API 面 (C# stub、C、tcs VM、.NET) の binding の構成と実装ライブラリの供給方針
- `docs/serve.md` — `lub --serve`(HTTP + SSE で web ホットリロード)。外部リポからゲームを書くテンプレートは `templates/game/`(C# の csproj。tcs VM の player と .NET 実行)
- `tcs/CLAUDE.md` — `tcs/`(C# の compiler と tcs VM)を触るときの、tcs に固有の指示
- `git log` — 実装経緯

## 流儀

- PR ベースで回す。作業はブランチ → PR。マージ gate は PR CI(`.github/workflows/ci.yml` の単一 workflow、required check は集約 job `gate` のみ)で、deploy は master push 時に同じ workflow が verify を通した上で行う。push は必ずユーザー承認後、merge は人間の act。
- テストの走り分け: commit フック = format + 空白 + docs lint(秒)。フル検証は PR CI が担う(linux job: `scripts/native-gate.sh` = docs lint/build/smoke/C# の runtime テスト (tcs VM)/C# gate、windows job: build + WARP golden、macos job: `scripts/apple-gate.sh --part mac` = build/smoke/C# の runtime テスト/Metal の golden(Linux の golden との許容差比較)/shader cache、web-build → web-verify(シャード)+ web-golden、linux と web-build の成果物で web-export(`scripts/web-export-gate.sh`)、formal job: `tests/formal/run.sh` = serve の接続の片づけとバッファの上限を全列挙と z3 で検査)。`tcs/` を変えた PR では tcs 自身のテストの job(`tcs-test`、`tcs-bench-2backend`、`tcs-compare-check`、`tcs-selfbench`、`tcs-native`)も走る。docs のみの変更は lint と format だけ、web のみの変更は native をスキップ(ci.yml の changes job)。手元でフル検証したいときだけ `scripts/pre-push.sh`(CI と同内容の手動ゲート)。iOS の simulator での実行(`scripts/apple-gate.sh --part ios`)は PR の CI に無く、release の workflow(`.github/workflows/release.yml`、`v*` の tag)で走る。rc は `v0.2.0-rc.1` のような tag で prerelease になる。
- 移植は理想設計で。原典 (NGS 等) のコード構造・ファイル分割・抽象化は真似しない。C# + lub 哲学から導いた最良案で書く。忠実に写すのは gameplay rule (敵パターン・弾数・HP・速度・出現タイミング) だけ。ファイル分割は概念単位 (Scene, EntityWorld, Atlas, Font, DrawList)、state machine は interface/enum/class、entity は List/Pool。
- ドキュメントは `docs/README.md` の方針に従う。live/record のディレクトリ分離、平易な語(jargon・完了注記・bold 禁止)、記録本文は当時のまま。機械検査は docs-lint。
- フォーマッタはツール標準デフォルト。既存スタイルに寄せる設定ファイルは置かない。例外は `.clang-format` の `ReflowComments: false` だけで、日本語のコメントを和欧の境目で折り返させないためのもの。コメントは 1 文を 1 行に書き、文の途中で折り返さない。clang-format=LLVM default、dotnet format=default、stylua=default、prettier=default。整形は `scripts/format.sh`(commit フックが `--changed` で変更ファイルを整形し、CI の format job が `--check` で検査する)。

## サンプル構成

各サンプルは `samples/<name>/{<Entry>.cs, <Entry>.csproj, data/...}` に自己完結。

- csproj は basename = entry class(`Hello00.csproj` → `Hello00`)
- ソースはデータを `Io.LoadText("samples/<name>/data/...")` と cwd 基準で参照
- サンプル横断で使うアセット(フォント等)は `samples/data/`(例: `samples/data/fonts/`)。サンプル固有のものだけ `samples/<name>/data/`
- tcs の出力は `samples/<name>/.lub/`(bytecode の `<Entry>.tcsb` と入力の一覧の `<Entry>.inputs`。`.lub/` は gitignore)
- C 側 bare-name 解決 (`src/main.c`) と web のサイト(`web/vite.config.ts` の `sample-assets.json`)も同じ規約で読む
- C# (TinyC#) が authoring 言語(サンプル一覧の正は `samples/` の `<Entry>.csproj` を持つディレクトリ)。実行は `lub samples/<name>/<Entry>.csproj`(tcs vm --watch + hotswap、要 dotnet SDK。compiler と VM は `tcs/`)。csproj は basename = entry class の規約で、lub は MSBuild 評価をしない(IDE 型チェック用の実ファイル)。check / bytecode への変換のみは `scripts/run-cs-sample.sh <name> --check|--vm`。共有 stub は `cs-lib/lub_stub.cs`(namespace `Lub` の下に `Gfx` / `Input` / `App` / ... と enum。cs-lib の実装は `Lub`(`Math.cs`)と `Lubx`(`lubx/`)の namespace。ゲームは `using Lub;`(lubx を使うなら `using Lubx;` も)で `Gfx.BeginPass(...)`)。C# は通常の命名(PascalCase)で書き、tcs が host の関数の snake_case の名前(`lub.gfx.begin_pass`)に写す。API 面は生成した tcs VM の host の関数(`src/gen/vm_api_gen.c`)。stub の検査と生成物は `tools/lub-gen`(`docs/api-glue.md`)。web はサイトの入口(lider)のメニューか `#sample=<name>`
- .NET 実行: 同じ C# ソースを実 .NET で動かす経路。`dotnet/Lub`(生成した facade + host、`App.Run(typeof(Game), args)`)が共有 library(`build-release-linux/liblub.so`、CMake の `lub_shared`)を P/Invoke する。サンプルは `dotnet run --project dotnet/SampleRunner -p:Sample=<name> -- --capture out.png`(要 `LUB_NATIVE_LIB` か出力隣の共有 library)。native gate が tcs VM の player と .NET の frame digest(`--digest`、C API 呼び出しの構造の hash)を比較する。Math は tcs の `TinySystem.props`(SampleRunner、`samples/`、`tests/cs`、`templates/game` が Import)で TinySystem.Math になり、tcs VM と同じ実装を通る。
- サンプルは実行例であってテスト対象ではない。サンプルに自己テストを書かず、samples 専用のテストも置かない。検査は `tests/cs/` の runtime テストと golden に限る(runtime に検証段が入るまでの方針)
- runtime テストは C# で書き、tcs を通して動かす(`tests/cs/<Entry>.cs` + 中身 1 行の `<Entry>.csproj`。一覧と変換は `scripts/cs-tests.sh`、1 本だけなら `lub tests/cs/<Entry>.csproj`)。判定は `tests/cs/Check.cs`: 失敗は終了コード 1、成功は `Check.Pass()`、期待するエラーは `Check.Error` の後に失敗する呼び出しを置き、entry の `OnError` で照合する。tcs の出力と runtime の間の層は利用者にも開発者にも開かない(C# 以外で test を書かない)
- 実装モジュール(lub.Math, lubx)は `cs-lib/` にあり、サンプルの一部という位置付け(runtime には持たせない)。binding の構成は `docs/api-glue.md`
- API の記述は `cs-lib/lub_stub.cs`。C API の header(`include/lub/lub_api.h`)、tcs VM の host の関数(`src/gen/vm_api_gen.c`)、API docs のデータ(`web/gen/lub-api-docs.json`)、.NET 実行の facade(`dotnet/Lub/Lub.g.cs` と、stub の `namespace Lubx` の分の `dotnet/Lub/Lubx.g.cs`)は生成物で手で編集しない。stub を変えたら `scripts/gen-api.sh` で再生成する(native gate が `--check` で差分を検査)。host の関数の名前(`lub.gfx.begin_pass`)は tcs の出力だけが呼ぶ内部の面

## web / WASM verify

- emcc は PATH に無いが存在する: `source ~/emsdk/emsdk_env.sh` で使える。`which emcc` だけで「無い」と早合点しない。
- build: `emcmake cmake --preset wasm-release` → `cmake --build build/wasm -j`。既存 build/wasm が Unix Makefiles だと preset(Ninja)の configure は mismatch で失敗するが、`cmake --build build/wasm` は既存設定で再ビルドできる。
- サイト (`web/`): 入口 (`index.html`、`web/lider/main.ts`) は lider を動かす page で、ゲームの C# は lider が player に組み込んだ tcs で変換する(`lider/README.md`)。ほかに player の page (`player.html`、エディタを通らない tcs VM の player。golden と tests/cs の検査が使う) と docs の page (`docs.html`)。`lider.html` は入口への転送。
- verify: `cd web && npm run dev`(localhost:5173)起動 → 別プロセスで `LUB_URL=http://localhost:5173/ npm run verify-lider`(入口: 起動、保存した編集の反映、診断、並べる表示、サンプル、メニューと Doc の page)と `LUB_URL=http://localhost:5173/ npm run verify`(player の page で `tests/cs` の runtime テストを動かす A9。事前に `scripts/cs-tests.sh` の `cs_tests_vm_build` で `tests/cs/.lub/all.tcsb` を作っておく)。どちらも playwright + chromium swiftshader。
- web golden: 同じ dev server に対して `LUB_URL=... npm run golden`。native golden と同じ curation(20 サンプル、frame 30/120/240、fixed-dt)を、native の tcs が変換したサンプルの bytecode(先に `scripts/run-cs-sample.sh <name> --vm`。撮るサンプルは `node scripts/golden-web.mjs --list`)を player の page に直接渡して wasm の --capture 経路(backend_webgpu の wg_capture)で撮り、`tests/golden/<name>_web.png` と byte 比較。golden は swiftshader 固有なので playwright/chromium を上げたら `npm run golden -- --update` で再生成。
- 入口の page は、保存された .cs を .NET の wasm(tcs の WasmCompiler の CSharpier、`web/lib/format.ts` の worker)で整形して書き戻す。tcs / WasmCompiler を変えたら `cd web && npm run gen-tcs -- --publish`: bundle は `web/tcs-wasm-assets/`(gitignore)。生成には dotnet SDK + wasm-tools workload が要る。`gen-tcs -- --publish` は AOT を有効にする。native Wasm が 25MiB を超える場合は分割し、worker の resource loader が内容を検証して結合する。
- lub.js 構文チェック: `node --check build/wasm/vm/lub.js`。
- docs サイト (`/docs.html`): ガイドは `docs/manual/*.md`、API reference は `cs-lib/lub_stub.cs` の XML doc が single source of truth。`scripts/gen-api.sh` が `web/gen/lub-api-docs.json` に固め、`web/scripts/gen-api-docs.mjs`(`npm run gen-api`、dev/build に組み込み済)が markdown を HTML にして `web/public/api-docs.json`(gitignore)を作り、`web/docs/main.ts` が描画する。
