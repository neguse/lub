# CLAUDE.md

lub は code-centric な game 制作 runtime。Haxe → Lua transpile + hot reload で
game を書き、native (DX12 / Vulkan 直接実装 + SDL3 GPU) と web (WASM + WebGPU)
の両方で動かす。

## 現状の source of truth

memory に状態を溜めない。現在地は常に以下を読む:

- `docs/roadmap.md` — Phase の定義と進捗(Phase 0/1 完了、Phase 2 Hakonotaiatari 進行中、Phase 3 未着手)
- `docs/design.md` — 設計方針
- `haxe-wasm/HANDOFF.md` — Haxe compiler の client-WASM 化(完遂: byte 一致 + web playground 統合)
- `docs/serve.md` — `lub --serve`(HTTP + SSE で web ホットリロード)。外部リポからゲームを書くテンプレートは `templates/game/`
- `git log` — 実装経緯

## 流儀

- **master 直コミット**。ブランチを切らない(「default ブランチなら branch first」はこのリポでは適用外)。push は必ずユーザー承認後。
- **移植は理想設計で**。原典 (NGS 等) のコード構造・ファイル分割・抽象化は真似しない。Haxe + lub 哲学から導いた最良案で書く。忠実に写すのは gameplay rule (敵パターン・弾数・HP・速度・出現タイミング) だけ。ファイル分割は概念単位 (Scene, EntityWorld, Atlas, Font, DrawList)、state machine は interface/enum/class、entity は Array/Pool。
- **フォーマッタはツール標準デフォルト**。既存スタイルに寄せる設定ファイル (`.clang-format` / `hxformat.json` / `.prettierrc`) は置かない。clang-format=LLVM default、haxe=default(tab)、prettier=default。整形は `scripts/format.sh`(`--check` で CI)。

## サンプル構成

各サンプルは `samples/<name>/{<ClassName>.hx, <name>.hxml, data/...}` に自己完結。

- hxml は `-cp samples/<name>` / `-main <ClassName>`
- hx はデータを `Io.loadText("samples/<name>/data/...")` と cwd 基準で参照
- 共有 Lua (`boot.lua`, `lub_io.lua`, `lub_prelude.lua`) は `samples/` 直下
- 生成 Lua は `samples/<name>/.lub/<name>.lua`(`.lub/` は gitignore)
- C 側 bare-name 解決 (`src/main.c`) と web playground (`web/playground/samples.ts`, `verify-headless.mjs`) も対応済
- **C# (TinyC#) サンプル**は `samples/<name>/<ClassName>.cs`。共有 stub は `cs-lib/lub_stub.cs`、変換・実行・check は `scripts/run-cs-sample.sh <name> [--build|--check|--watch]`(要 dotnet SDK + `third_party/tcs` submodule)。API 面は `samples/lub_prelude.lua` が注入する namespace table で Haxe と共通

## web / WASM verify

- **emcc は PATH に無いが存在する**: `source ~/emsdk/emsdk_env.sh` で使える。`which emcc` だけで「無い」と早合点しない。
- build: `emcmake cmake --preset wasm-release` → `cmake --build build/wasm -j`。既存 build/wasm が Unix Makefiles だと preset(Ninja)の configure は mismatch で失敗するが、`cmake --build build/wasm` は既存設定で再ビルドできる。
- verify: `cd web && npm run dev`(localhost:5173)起動 → 別プロセスで `LUB_URL=http://localhost:5173/ npm run verify`(playwright + chromium swiftshader)。A1-A4=初期描画/hot reload、A5=全サンプル切替。
- **haxe-lib を変えたら `cd web && npm run gen-haxe`**: in-browser コンパイラの lub ライブラリは `web/public/haxe-wasm/std-bundle.json`(gitignore)に焼き込みなので、再生成しないと web 側だけ古い lubx でコンパイルされる(A5 は nonBlack 判定が甘く、コンパイル失敗しても前サンプルの絵で PASS しうる。playerReady timeout 警告が出たら疑う)。
- verify が「compiling…」のままハングしたら vite dev server の詰まりを疑って再起動(長時間稼働 + 大量ファイル変更で worker モジュール変換が無音で止まることがある)。
- lub.js 構文チェック: `node --check build/wasm/lub.js`。
- **docs サイト** (`/docs.html`): ガイドは `docs/manual/*.md`、API reference は haxe doc comment が single source of truth。`web/scripts/gen-api-docs.mjs`(`npm run gen-api`、dev/build に組み込み済、要 haxe CLI)が `haxe --xml` から `web/public/api-docs.json`(gitignore)を生成し、`web/playground/docs.ts` が描画する。
