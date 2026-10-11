# lub playground (web)

ブラウザで lub サンプルの C# ソースを編集 → client-only でその場コンパイル
(.NET wasm 化した TinyC# コンパイラ)→ player iframe にホットリロードする
プレイグラウンド。

サーバ不要・完全静的。C#→Lua コンパイルもブラウザ内で完結する。

## WebXR

`xr.mjs` の `createXR({ canvas, button, getModule, report })` を Wasm の読み込み前に
呼び、初期化後に戻り値の `ready()` を呼ぶと、immersive-vr の開始・終了を扱える。
`getModule` は Emscripten Module を返す関数。必要なら `maxDimension` で片眼の
最大辺を制限し、`unlockAudio` にユーザー操作時の音声再開処理を渡す。

ゲームは native と同じ `Lub.Xr.Active` / `View` / `Input` / `Focused` を使う。
視点行列・生の入力・片眼の描画先 (`XrView.Target`) の接続を Lub が担当し、
カメラやボタンの意味付けはゲームに置く。片眼の sRGB 出力を左右に並べ、WebGL の
XR レイヤーへ転送する。XR セッション中は SDL の更新も XR フレーム内で実行する。

## 前提アセット(ローカルビルド由来・gitignore)

`npm run dev` / `build` の前に用意する:

```bash
# 1. lub player wasm(build/wasm/lub.{js,wasm,data}) … リポジトリルートで C ビルド
source ~/emsdk/emsdk_env.sh             # emcc / emcmake を PATH に
emcmake cmake -S . -B build/wasm        # WGPU + emdawnwebgpu port が configure される
cmake --build build/wasm -j             # lub.{js,wasm,data} が生成
# 2. slang-wasm(web/public/slang/…) … シェーダコンパイラ
npm run fetch-slang          # postinstall でも走る
# 3. TinyC# コンパイラ wasm 一式(web/tcs-wasm-assets/ と web/tcs-prebuilt/)
#    要 dotnet SDK + wasm-tools workload + third_party/tcs submodule
npm run gen-tcs -- --publish # tcs の .NET wasm bundle を web/tcs-wasm-assets/ に固める
npm run gen-tcs-prebuilt     # cold 起動用 prebuilt snapshot(cs-lib / C# サンプル変更時も再生成)
```

## コンパイルの流れ

- `playground/tcs-compiler.ts` … .NET wasm 化した TinyC#(tcs)を増分 session
  (`SessionExports`)で動かし、変更 `.cs` のみ Update → registry apply する単一 entry Lua を返す。
  補完・hover・診断も同じ session が提供する。
- `playground/diagnostics.ts` … tcs の診断をパースしてエディタ内に表示。
- `playground/samples.ts` … `.cs` をロードし、compile 後の `.lua` を scan して data files
  (slang 等)を解決。サンプル一覧の正は `CS_SAMPLES`。
- `playground/main.ts` … boot とサンプル切替で compile→player 起動、ソース編集を debounce→
  再 compile→`syncFiles`。data(slang)編集は compile 不要で直接 sync。

## 実行時アーキテクチャ

```
            parent (index.html / main.ts)              iframe (player.html / player.ts)
            ┌────────────────────────────┐             ┌──────────────────────────────────┐
            │ CodeMirror editor          │   setFiles  │ slang-bridge.ts                  │
            │   path -> content table    │  ────────▶  │   window.slangCompile() を export │
            │ sample dropdown / restart  │  syncFiles  │ WebGPU device 取得 → preinit     │
            │ debounce 75ms              │  ────────▶  │ lub.js (Emscripten module)     │
            └────────────────────────────┘  ◀─player──│   ↑ EM_ASYNC_JS bridge            │
                                            Ready/log │   ↑ FS.writeFile で MEMFS overlay │
                                                       │ backend_webgpu — canvas へ描画   │
                                                       └──────────────────────────────────┘
```

postMessage プロトコル:

- `parent → iframe`: `setFiles {files, entry}` (初回ブート時 1 回), `syncFiles {files}` (編集毎)
- `iframe → parent`: `playerReady` (ハンドシェイク), `runtimeReady` (wasm main が FS 公開後の
  第二ハンドシェイク), `log {level, msg}` (console relay)

shader compile は C 側 (`src/shader.cpp`) の `EM_ASYNC_JS` shim から
`window.slangCompile(src, entry, stage)` を呼び、`{wgsl, reflectJson}` を `'\x01'`
区切りで pack して戻す。エラーは `'\x02' + msg` 形式で Slang diagnostic として
err_buf に届く。

MEMFS sync: iframe 側で Emscripten の data file package (`lub.data`) をマウント
した直後に `FS.writeFile` でエディタ内容を上書きする (`player.ts` の `postPreload`
hook)。実行中の `syncFiles` も同じ `FS.writeFile` 経路で、C 側は次フレームの
`stat()` で mtime 違いを検知して reload する (native と同じ hot-reload コード)。

## コマンド

```bash
npm run dev               # Vite dev server (http://localhost:5173/)
npm run build             # 本番ビルド -> dist/
npm run verify            # headless Chromium で end-to-end 検証(別ターミナルで dev を起動しておく)
npm run golden            # web golden(native と同 curation を wasm --capture 経路で byte 比較)
npm run gen-api           # docs サイト用 API reference JSON を再生成(dev/build にも組み込み済)
npm run gen-tcs -- --publish  # TinyC# コンパイラ wasm を再生成(tcs 変更時)
npm run gen-tcs-prebuilt  # C# prebuilt snapshot を再生成(cs-lib / C# サンプル変更時)
npm run format            # prettier(format:check は CI 用)
npm run deploy            # build + wrangler deploy
```

## Headless verification

`npm run verify`(`scripts/verify-headless.mjs`)は playwright + chromium
(swiftshader Vulkan) で:

1. sample 01 の初期描画 (orange triangle on dark blue clear) を pixel bucket で確認
2. fragment shader を編集 → green になる
3. `.cs` の ClearColor を編集 → 再 compile → 背景が red になる
4. verts を縮小編集 → green pixel 数が減る
5. 登録済み sample を順に切替 → 各サンプルの非黒描画を確認
6. C# 増分編集が runtime の commit ACK(synced rev 表示)まで貫通する
7. C# の診断がエディタ内に表示される
8. C# 補完 / hover が返る(レイテンシ観測ログ付き)

スクリーンショットは `/tmp/lub-verify/` に出力される。CI 利用時は dev server を
別ジョブで立ち上げてから `LUB_URL=http://...` を指定すること。サブパス配信の確認は
`--base /lub/` でビルドした dist を preview し、`LUB_URL` をそのサブパス
(`http://localhost:4174/lub/`)にして `LUB_VERIFY_STRICT_404=1` を付けて実行する
(同一 origin の 404 を失敗にする)。

## Browser requirements

- WebGPU が利用可能なブラウザ:
  - Chrome / Edge (primary、137+) — 既定で WebGPU 有効。
  - Safari (iPadOS / iOS / macOS 26+) — WebGPU を利用可能。
  - Firefox Nightly — `dom.webgpu.enabled` を `about:config` で有効化。
- ローカル開発: Vite dev server が emdawnwebgpu に必要な CORS/MIME 設定を済ませる。
- production bundle (`npm run build`) は `web/dist/` 配下。資源 (`wasm/`, `slang/`,
  `samples/` など) は `import.meta.env.BASE_URL` 基準で読むので、サブパスに置くときは
  `npx vite build --base /lub/` のように base を付けてビルドする(`vite preview --base /lub/`
  で確認できる)。

## Live edit caveats / limitations

- shader に syntax error がある場合: 既存の shader を維持して Slang diagnostic を
  iframe log に流すのみ (next save で復帰)。初回 compile 失敗のみ load を止める。
- 75ms debounce: 入力後 75ms 静止してから `syncFiles` を送る。連打中は更新されない。
- サンプル切替時に dirty な編集があると `confirm()` で警告する。
- `--capture` の swapchain capture は native のみ。web (webgpu backend) では
  任意 render target の readback (`Gfx.readback(key)`) を使う。
- sdlgpu backend は web 非対応。web は `webgpu` のみ。
