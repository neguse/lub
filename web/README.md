# lub playground (web)

ブラウザで lub サンプルの **Haxe ソース(`.hx`)を編集 → client-only でその場コンパイル
(WebAssembly 化した Haxe コンパイラ)→ player iframe にホットリロード**するプレイグラウンド。

サーバ不要・完全静的。Haxe→Lua コンパイルもブラウザ内(Web Worker)で完結する。

## 前提アセット(ローカルビルド由来・gitignore)

`npm run dev` / `build` の前に 3 つ用意する:

```bash
# 1. lub player wasm(build/wasm/lub.{js,wasm,data}) … リポジトリルートで C ビルド
#    (emcc。~/emsdk/emsdk_env.sh を source した上で通常のビルド)
# 2. slang-wasm(web/public/slang/…) … シェーダコンパイラ
npm run fetch-slang          # postinstall でも走る
# 3. Haxe コンパイラ wasm 一式(web/public/haxe-wasm/…) … spike の成果物から生成
#    先に spike をビルドしておくこと(spike/build.sh または spike/harness/iter.sh)。
npm run gen-haxe             # spike/dist or spike/build から glue+wasm+std+prelude を固める
```

`npm run gen-haxe`(`scripts/gen-haxe-assets.mjs`)は spike(`spike/`)が出力した
`haxe.js`(patched glue)+ `code-*.wasm` + Haxe std + lub externs + prelude を
`public/haxe-wasm/{haxe.js, *.wasm, std-bundle.json, manifest.json}` に固める(~18MB、gitignore)。

## コンパイルの流れ

- `playground/haxe-compiler.ts` … main-thread API。`compileHaxe(files, mainClass)` で
  Web Worker(`haxe-compiler.worker.ts`)に投げ、native の `haxe_build.c` と同じ連結
  (`HAXE_PRELUDE + raw + "\nreturn <Main>\n"`)で player が読める `.lua` を返す。
- worker は未改変の wsoo glue を「Node 擬装(`process`/`require`)+ in-memory VFS(node:fs sync
  サブセット)」で動かす(spike の `spike/harness/browser/` と同方式)。WebAssembly.Module は
  1 回だけコンパイルしてキャッシュ、compile ごとに fresh instance を起こす。
- `playground/samples.ts` … `.hx`/`.hxml` をロードし、compile 後の `.lua` を scan して data files
  (slang 等)を解決。
- `playground/main.ts` … boot とサンプル切替で compile→player 起動、`.hx`/`.hxml` 編集を debounce→
  再 compile→`syncFiles`。data(slang)編集は compile 不要で直接 sync。

## コマンド

```bash
npm run dev       # Vite dev server (http://localhost:5173/)
npm run build     # 本番ビルド -> dist/(public/haxe-wasm も同梱)
npm run verify    # headless Chromium で end-to-end 検証(別ターミナルで dev を起動しておく)
npm run deploy    # build + wrangler deploy
```

`npm run verify`(`scripts/verify-headless.mjs`)は 01_triangle の初期描画・シェーダ編集・
**Haxe ソース編集(`.hx` の clear_color を書き換え→再 compile→赤背景)**・verts 編集・
全サンプル描画を WebGPU(swiftshader)で検証する。
