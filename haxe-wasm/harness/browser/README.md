# ブラウザ実機ハーネス(WasmGC / 仮想FS)

wasm 化した Haxe コンパイラを **実ブラウザ(WasmGC)** で走らせ、`00_hello.hx → .lua` が
native golden と**バイト一致**することを確認する(plan.md 手順6 後半 / HANDOFF task 8)。

## 使い方

```bash
# 前提: haxe-wasm/build/wasm/{haxe.js, haxe.assets/*.wasm} と haxe-wasm/build/std が存在すること
#       (haxe-wasm/harness/iter.sh を一度通すか、scripts/03_wasm.sh で生成)
node haxe-wasm/harness/browser/run.mjs
# → ★ BROWSER WASM == NATIVE GOLDEN: IDENTICAL ✓
```

`run.mjs` は (1) 必要なら `pack_fs.mjs` で `fs-bundle.json` を生成、(2) `patch_env.mjs` で
glue に env パッチを適用、(3) ローカル http で配信、(4) Playwright(repo の `web/node_modules`
から解決)で **システムの Chromium/Chrome**(WasmGC 対応版)を headless 起動して検証する。

## 仕組み(なぜ未改変 glue がブラウザで動くか)

wsoo が吐く glue(`haxe.js`)は `process.versions.node` が truthy だと **Node 経路**
(`require("node:fs")` 同期 API でファイル I/O、wasm は `require("node:fs/promises").readFile`
で取得)を使う。ブラウザには fs が無いので:

- **`node-shim.js`**: `globalThis.process` と `globalThis.require` を擬装し、`node:fs`/`fs/promises`/
  `path`/`os`/`tty`/`util` を **in-memory VFS**(`globalThis.__VFS`)上に実装する。glue が呼ぶ
  device メソッド(open/read/write/stat/lstat/readdir/opendir/...)が委譲する `f.xxxSync` を賄う。
  Node Buffer は使わず Uint8Array I/O。完了は `process.exit` で検知。
- **`pack_fs.mjs`**: `build/std`(→`/std`)+ `haxe-lib/lub`(→`/lub`)+ `samples/00_hello`(→`/sample`)を
  base64 で `fs-bundle.json` に固める。
- **`index.html`**: バンドルと wasm を fetch して VFS へ載せ、`process.argv`/`HAXE_STD_PATH`/
  `require.main.filename` を設定し、**未改変の `haxe.js` を `<script>` で読み込んで自動実行**。
  出力 `/work/out.lua` を回収する。
- **`run.mjs`**: 上記を配信し Playwright で実行、出力を golden とバイト比較。

### ハマりどころ

- **cwd を `/work`(std サブディレクトリを持たない場所)にする**。`/` のままだと std を `/std` に
  マウントした結果 `std/Date.hx` が cwd 相対で見つかり、Haxe の `std.Date`(lua/Boot.hx 等)が
  実 std ファイルへ誤解決して `package; should be package std` になる。native は cwd に std/ が
  無いので起きない。
- glue は **patch_env 済(integers_*_size / caml_thread_initialize / thread・mutex no-op)** を使う
  (Node の iter.sh と同じ)。パッチ内容は純 JS でブラウザでも安全。
- Playwright 同梱ブラウザのビルド番号がキャッシュと食い違う環境向けに、`run.mjs` は
  `executablePath` でシステム Chromium/Chrome(WasmGC 対応版)を使う。

このハーネスの VFS + 擬装 + 未改変 glue 実行は、そのまま web playground の **client-only
`compileHaxe()` の核**として流用できる(`plan.md` §共通)。
