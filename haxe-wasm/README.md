# Phase 0 spike: Haxe compiler を WASM 化して client-only compile できるか

`plan.md` の feasibility spike 実施記録と GO/NO-GO 勧告 + 全自動ビルド。

## 結論: **GO(client WASM 採用を推奨)**

- 最大の go/no-go であった **domain/マルチコア問題は GREEN**(単一 domain で実 compile 処理まで実行)。
- wasm_of_ocaml への compile は成功、**コア compiler primitive の欠落はゼロ**。
- 追加で必要な C primitive は周辺ライブラリの **module-init だけ**。順に pure-OCaml 化して
  クリア(pcre2 / extc / sha / integers / ctypes-PosixTypes / ctypes-memory /
  luv-version / luv-stubs / **systhreads(Thread.t を pure-OCaml 差し替え)**
  / stdlib Mutex(wsoo sync.wat)/ thread-init)。
- **byte 一致 達成(2026-06-02)**: `bash harness/iter.sh` が
  `WASM == NATIVE GOLDEN: IDENTICAL ✓`(24331B、node exit 0)。最後の2壁を解いた:
  1. **luv unlink**: luv の libuv コールバック trampoline 登録は **C→OCaml 関数ポインタ
     (libffi / ctypes Foreign funptr coerce)を要するが wasm に libffi が無く wsoo では原理的に
     不可**(`luv_get_*_trampoline` 直後に wasm `unreachable`)。luv は **eval/マクロ専用**で
     macro 無し compile には不要 → **`src/dune` から luv を外し eval を stub 化**(§Step6-7)。
  2. **thread-local-storage(domainslib/saturn 依存)の Thread.t 表現**: luv が先に trap して
     いたため隠れていた第2の壁。`thread_local_storage.ml:5` が `Thread.self()` を「field 1=unit の
     ブロック」前提に検査+TLS を field 1 に Obj.magic 格納する。systhreads stub の `Thread.t=int`
     (即値)だと `Obj.field` が wasm で `unreachable` → **Thread.t を 3 フィールドブロックに変更**。
- バンドル ~4MB(gzip)/ cold ~185ms と許容範囲。

## 全自動ビルド(`build.sh` / `Dockerfile`)

```
haxe-wasm/build.sh
```
で `Dockerfile` を build し、wasm Haxe(`haxe.js` + `haxe.assets/*.wasm` + `std/`)を
`haxe-wasm/dist/` に抽出 → caml_thread_initialize を env-patch(no-op)→ `00_hello` を
ホスト Node で compile して native golden と byte 比較する。Dockerfile が行うこと:

1. system deps(`libmbedtls-dev` は opam depext が拾わない)。
2. binaryen **version_130**(apt 版 108 は `wasm-merge` 欠落で wsoo runtime ビルド不可)。
3. `wasm_of_ocaml-compiler` 6.3.2。
4. `scripts/patch_opam_libs.sh` で sha/integers/ctypes/luv を pure-OCaml 化して pin
   (deps-only の前に pin → patched 版が入る)。
5. Haxe `5.0.0-preview.1` を clone し `patches/haxe-5.0.0-preview.1-wasm.diff` を適用。
6. `opam install . --deps-only` + `dune build src/haxe.bc`。
7. `wasm_of_ocaml compile --effects=cps` で wasm 化、`std/` 同梱。

## 環境 / バージョン

- 隔離 Docker `ocaml/opam:debian-12-ocaml-5.2`(OCaml 5.2.1 / opam 2.1.6 / dune 3.23.1)。
- Haxe `5.0.0-preview.1`(`git describe` 確認)。wasm_of_ocaml 6.3.2(`--effects=cps`)。
- ハーネスはホスト Node v26(wsoo 要求 Node≥22)。

## 各ステップ

### Step1 native + golden(✅)
- lub externs(`@:native`/`@:luaRequire`/`@:multiReturn`)は Haxe5 で型チェック通過。
- gotcha: `libs/mbedtls` が system `mbedtls/error.h` を直 #include → `libmbedtls-dev` 必須。
- golden: `haxe -cp haxe-lib/lub -cp samples/00_hello -main Hello00 --lua out.raw`。
  24331B / sha256 `d5c975ec27a761e07aba06b6e05f616a713984863e5049636f51ed3111db5633`。
  prelude/`return Hello00` は host(`embedded_prelude.h`)が連結する定数で raw には無い。

### Step2 haxe.bc(✅)
- `src/dune` の `; (modes byte)` を `(modes byte exe)` 化 → `dune build src/haxe.bc`(45MB)。

### Step3 wasm_of_ocaml(✅)
- compile 成功(28s、glue 68KB + wasm 11MB)。リンク時 missing ~217 は全て周辺
  (`results/missing_primitives.txt`)。コア compiler primitive の欠落ゼロ。

### Step4-5 実呼出 primitive / domain 判定(✅ = 核心)
`harness/trace_env.mjs` で全 throw スタブをトレーサ化し、実際に呼ばれる primitive を全列挙。
**全て周辺ライブラリの module-init**(出力には無影響):

| 群 | 実体 | 対処 | 状態 |
|---|---|---|---|
| pcre2 init | `pcre2_ocaml_init`/`version`/`config_*`、`def_rex` | Haxe-vendored を pure-OCaml + lazy 化 | ✅ |
| extc | `get_full_path`/`filetime`(`Unix.stat`)/`time` 等 | Haxe-vendored を pure-OCaml | ✅ |
| sha | `let zero = string ""` の SHA1 計算 | pin して pure-OCaml SHA1 | ✅ |
| integers | `unsigned_init`/size/UInt32/64 変換 | pin して Int32/Int64 で pure-OCaml | ✅ |
| ctypes | PosixTypes `typeof_*`(arithmetic バリアント) | pin して定数バリアント | ✅ |
| luv | `Version.suffix`(libuv 版) | pin して定数 | ✅ |
| ctypes-memory | `ctypes_allocate`/`block_address`/`write`/`memcpy`(luv が libuv 構造体を確保) | pin して pure-OCaml ダミー(実行時に読まれない) | ✅ |
| systhreads | `caml_thread_self`/`id`/`new`/`join`/`yield`(Thread.t は custom block。eval が Thread.self() を呼ぶ) | **threads.cma を pure-OCaml stub に差し替え**。Thread.t は thread-local-storage 互換の 3 フィールドブロック。stdlib Mutex は wsoo sync.wat 実装済 | ✅ |
| **luv trampoline** | `luv_get_*_trampoline`(libuv コールバックの C→OCaml funptr) | **libffi 必須で wsoo 不可 → `src/dune` から luv unlink + eval stub 化** | ✅ |
| **thread-local-storage** | `thread_local_storage.ml:5` が Thread.self() を Obj.magic で field 1 に TLS 格納する前提で検査(`Thread.t=int` だと `Obj.field` が wasm `unreachable`) | **threads stub の Thread.t を `{_id;mutable _tls;_other}` の 3 フィールドブロック化**(self() は安定インスタンス) | ✅ |

- **domain 判定 = GREEN**: `-D enable-parallelism` 無しの単一 domain で module init → 引数解析
  → std ロード → 実 compile 処理まで進行。domain primitive 由来 crash 皆無。
  `caml_thread_initialize` は no-op で通過(thread を spawn しない)。
- Haxe コンパイラ本体の regex は `Str`(wsoo 実装済)で pcre2 は EReg 用のみ。
- ctypes のポインタ返却関数(`ctypes_allocate`/`read`/`write`)は **0 ダミーだと wasm の
  illegal cast**(boxed 表現)で env-patch 不可。luv が libuv ハンドルを ctypes で確保する
  init 経路がこれを引くのが最後の壁。

### Step6-7 byte一致 / ブラウザ / コスト
- コスト(✅): wasm **3.0MB gz** + std **1.0MB gz** = ~4MB gz。cold ~185ms。
- **byte一致(✅)**: luv unlink + thread-local-storage 互換の Thread.t 化で達成。
  `bash harness/iter.sh` → `WASM == NATIVE GOLDEN: IDENTICAL ✓`(24331B、node exit 0)。
- ブラウザ(次): Node は実 fs を使うため疑似FS不要。ブラウザは std/externs/sample を
  仮想FSに載せる配線(`plan.md` の web 配線 §共通 に既述)。`harness/browser/` 参照。

## 残作業(byte一致 達成後)

1. ~~luv を Haxe から unlink/stub~~ **完了**(`src/dune` から luv を外し eval を stub 化)。
   実装: `evalLuv.ml` を 47個の `*_fields=[]` のみの stub に丸ごと置換、`evalValue.ml` の
   vhandle を全 `of unit` 化 + `same_handle` の `Luv.Thread.equal`→`h1==h2`、`evalStdLib.ml` の
   直接 Luv 参照 2 箇所、`evalMain.ml:147` の `Luv.Error.set_on_unhandled_exception` 除去。
   luv 除去で消える transitive dep `integers` を `src/dune` に明示追加。
2. ~~byte一致確認~~ **完了**(上記 iter.sh)。
3. **ブラウザ実機(WasmGC: Chrome/Firefox/Safari)**: std/externs/sample を仮想FSに載せる配線。
   `harness/browser/`(headless Chromium で同 golden とバイト一致を確認)。
4. **web playground 配線**(`web/playground/*`): `plan.md` §共通 をそのまま差し込む。
5. 最終採用時は native 側も Haxe5 に揃え二重バージョン drift を回避。

## ここまでで到達した実行段階

wasm Haxe は **全モジュール init を通過**(pcre2/extc/sha/integers/ctypes/systhreads/
thread-local-storage/domainslib/mutex)し、**`00_hello.hx → .lua` を native golden と
バイト一致で生成**する(luv は unlink 済で trampoline 経路に到達しない)。
それ以外の missing primitive は皆無。`harness/patch_env.mjs` が systhreads-init / integers-size /
luv-stub(非ゼロ funptr ダミー)を後段 env-patch する(systhreads は patch_threads.sh で
ソース差し替え済のため env では caml_thread_initialize のみ)。

## 成果物

- `Dockerfile` / `build.sh` … 全自動ビルド + 成果物抽出 + 検証
- `scripts/01_build_native.sh`・`02_build_bytecode.sh`・`03_wasm.sh`
- `scripts/patch_opam_libs.sh` … sha/integers/ctypes/luv を pure-OCaml 化して pin
- `scripts/patch_threads.sh` … systhreads(threads.cma)を pure-OCaml stub に差し替え
- `patches/haxe-5.0.0-preview.1-wasm.diff` … Haxe ソース改変一式(pcre2/extc pure-OCaml 化 +
  src/dune byte mode + **luv unlink + integers 明示 + eval stub 化**)
- `patches/{sha1.ml,unsigned.ml,ctypes-posixTypes.ml,ctypes-memory_stubs.ml,luv-version.ml}`
  … opam lib pure-OCaml 化
- `patches/{threads-thread.ml,threads-event.ml}` … systhreads stub(Thread.t は
  thread-local-storage 互換の 3 フィールドブロック)
- `harness/{run_node.sh,iter.sh,patch_env.mjs,trace_env.mjs}` … Node+wasm 実行・トレース・env-patch
- `harness/browser/` … ブラウザ実機検証(仮想FS + headless Chromium、native golden とバイト一致)
- `results/{missing_primitives.txt,actually_called_primitives.txt}`
- (gitignore: `build/`・`dist/` 等の重い生成物)
