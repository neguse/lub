# lub の web のサイト

[lub.neguse.net](https://lub.neguse.net) の page 一式。サーバ不要・完全静的。

- `index.html` (`lider/main.ts`): サイトの入口。ブラウザの中でエディタ (lider、`../lider/`) を動かす。
  ゲームの C# は lider が player に組み込んだ tcs で変換し、別の app として開く。
  project は MEMFS に置き、保存のたびに IndexedDB に写す。保存された `.cs` は page が整形して書き戻す (`lib/format.ts`)。
  右下のメニューから、Doc の page、サンプル (`#sample=<名前>`)、zip の保存と読み込みを開く。`?gist=<id か URL>` で公開の gist を開く。
- `player.html` (`player/main.ts`): エディタを通らない tcs VM の player (`build/wasm/vm/lub.js`)。
  page を開いて postMessage (`setFiles`) で bytecode と file を渡すと起動する。`tests/cs` の検査が使う。
- `docs.html` (`docs/main.ts`): ガイド (`docs/manual/*.md`) と API reference (`cs-lib/lub_stub.cs` の doc comment)。
- `lider.html`: 前の lider の page の URL。query と hash を付けたまま入口へ移る。

## 前提アセット(ローカルビルド由来・gitignore)

`npm run dev` / `build` の前に用意する:

```bash
# 1. lub の player の wasm (build/wasm/vm/lub.{js,wasm}) … リポジトリルートで C ビルド
source ~/emsdk/emsdk_env.sh             # emcc / emcmake を PATH に
emcmake cmake --preset wasm-release
cmake --build build/wasm -j
# 2. lider の player の wasm (lider/build/player-web/web/lub.{js,wasm}) … tcs を組み込んだ player
../lider/scripts/build-player.sh --web  # 要 rustup の wasm32-unknown-emscripten と dotnet
# 3. slang-wasm (web/public/slang/…) … シェーダコンパイラ
npm run fetch-slang                     # postinstall でも走る
# 4. 整形の .NET wasm 一式 (web/tcs-wasm-assets/) … 要 dotnet SDK + wasm-tools workload
npm run gen-tcs -- --publish
```

lider の bytecode (`../lider/build/web/Lider.tcsb`) は `npm run dev` / `build` が `npm run gen-lider` で作る。

`gen-tcs -- --publish` は AOT を有効にする。
25MiB を超える native Wasm は 8MiB 以下のファイルに分け、`native-wasm.json` に URL と内容の hash を記録する。
worker は各ファイルを検証してから結合した stream を .NET に渡すため、Cloudflare Assets のファイルサイズ上限内で静的配信できる。

## shader と MEMFS

shader compile は C 側 (`src/shader.cpp`) の `EM_JS` shim から `window.slangCompile(src, entry, stage)` (`lib/slang-bridge.ts`) を同期で呼び、`{wgsl, reflectJson}` を `'\x01'` 区切りで pack して戻す。
非同期なのは slang-wasm の読み込みだけなので、page は読み込みを済ませてから lub.js を起動する。
エラーは `'\x02' + msg` 形式で Slang diagnostic として err_buf に届く。

runtime の `Io` は MEMFS にあるファイルだけを読み、自分では取りに行かない。
page は main() の前に、ゲームが読み得るファイルを fetch して `FS.writeFile` で置く (置き終わるまで run dependency で main() を待たせる)。
サンプルの file の一覧 (`sample-assets.json`。`samples/<name>/**` の data と `.cs`、`samples/data/**`、entry class) は、dev では vite のミドルウェアが要求ごとに作り、build では `dist/` に書き出す (`vite.config.ts`)。
C 側は次フレームの `stat()` で mtime 違いを検知して reload する (native と同じ hot-reload コード)。

## コマンド

```bash
npm run dev               # Vite dev server (http://localhost:5173/)
npm run build             # 本番ビルド -> dist/
npm run verify-lider      # 入口の page の検査 (別ターミナルで dev を起動しておく)
npm run verify            # player の page で tests/cs の runtime テストを動かす
npm run gen-api           # docs サイト用 API reference JSON を再生成 (dev/build にも組み込み済)
npm run gen-tcs -- --publish  # 整形の .NET wasm を再生成 (tcs 変更時)
npm run format            # prettier (format:check は CI 用)
npm run deploy            # build + wrangler deploy
```

## Headless verification

どれも playwright + chromium (swiftshader) で、`LUB_URL` に dev server か preview の URL を渡す。
ブラウザの解決順は `scripts/lib/browser.mjs` (`CHROMIUM_PATH` → playwright の版固定 chromium → system chromium)。

- `npm run verify-lider` (`scripts/verify-lider.mjs`): 入口の page。起動、保存した編集の反映、通らない build の診断、通らない project での起動、並べる表示 (F10)、サンプル、メニューと Doc の page、保存した .cs の整形 (.NET の wasm)、同じ origin の 404。
- `npm run verify` (`scripts/verify-player.mjs`): `tests/cs` の test を全部 player の page で走らせ、終了コードを確かめる (A9)。
  走らせない test とその理由は `scripts/cs-tests.sh` の `cs_web_skip_tests`。
  事前に `scripts/cs-tests.sh` の `cs_tests_vm_build` で `tests/cs/.lub/all.tcsb` を作っておく。

## Browser requirements

- 対応ブラウザは `docs/manual/10-web.md`。
- ローカル開発: Vite dev server が emdawnwebgpu に必要な CORS/MIME 設定を済ませる。
- production bundle (`npm run build`) は `web/dist/` 配下。資源 (`wasm/`, `slang/`,
  `samples/` など) は `import.meta.env.BASE_URL` 基準で読むので、サブパスに置くときは
  `npx vite build --base /lub/` のように base を付けてビルドする(`vite preview --base /lub/`
  で確認できる)。

## 制約

- shader に syntax error がある場合: 既存の shader を維持して Slang diagnostic を log に流すのみ (次の保存で復帰)。初回 compile 失敗のみ load を止める。
- web の `--capture` は、指定した frame の swapchain を buffer に copy し、map が終わるまで次の frame を始めずに待ってから PNG を書く。
- sdlgpu backend は web 非対応。web は `webgpu` のみ。
