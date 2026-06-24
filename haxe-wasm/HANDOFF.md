# 引き継ぎ: Haxe→WASM spike(**byte一致 達成 / GO 確定**)

最終更新 2026-06-02。詳細な調査結果は [README.md](README.md) を参照。本書は spike の到達点と、
**この先(製品化)の作業手順**をまとめる。

## TL;DR

- 目的: Haxe 5 compiler を wasm_of_ocaml で wasm 化し、ブラウザ単体で `.hx → .lua` compile。
- 結論: **GO**。domain GREEN、コア primitive 欠落ゼロ、バンドル ~4MB gz / cold ~185ms。
- **byte一致 達成(Node + 実ブラウザ)**:
  - `bash haxe-wasm/harness/iter.sh` → `WASM == NATIVE GOLDEN: IDENTICAL ✓`(24331B、Node)。
  - `node haxe-wasm/harness/browser/run.mjs` → `★ BROWSER WASM == NATIVE GOLDEN: IDENTICAL ✓`
    (headless Chromium / WasmGC、仮想FS)。
- **Dockerfile からの end-to-end 再現も byte一致**(`bash haxe-wasm/build.sh` → `★ verify OK`)。
- 最後の2壁(luv trampoline / thread-local-storage の Thread.t 表現)は解決済(下記)。

## 達成までに解いた壁

### 1. luv unlink(byte一致の主要壁)
luv の libuv コールバック trampoline 登録は **C→OCaml 関数ポインタ(libffi / ctypes Foreign
funptr coerce)を要するが、wasm に libffi が無く wsoo では原理的に不可**(`luv_get_*_trampoline`
直後に wasm `unreachable`)。luv は eval/マクロ専用で macro 無し compile には不要。
**解1(採用): `src/dune` から luv を外し eval を stub 化**:
- `evalLuv.ml`(2452行)→ 47個の `*_fields = []` だけの stub に丸ごと置換(外部参照面はこの47個のみ)。
- `evalValue.ml` の vhandle 全コンストラクタを `of unit` 化 + `same_handle` の
  `Luv.Thread.equal h1 h2` → `h1 == h2`。
- `evalStdLib.ml` の直接 Luv 参照 2 箇所(`Luv.Env.unsetenv` / `Luv.Loop.Option.sigprof`)を脱 Luv 化。
- `evalMain.ml:147` の `Luv.Error.set_on_unhandled_exception` ブロック削除。
- luv 除去で消える transitive dep `integers`(Signed/Unsigned。evalValue 等が直接使用)を
  `src/dune` に**明示追加**(ctypes/luv は引かない)。
- すべて `patches/haxe-5.0.0-preview.1-wasm.diff` に統合済。

### 2. thread-local-storage の Thread.t 表現(隠れていた第2壁)
luv が先に trap していたため隠れていた。domainslib/saturn の依存 `thread-local-storage` は
`thread_local_storage.ml:5` で `assert (Obj.field (Obj.repr (Thread.self())) 1 = Obj.repr ())` と
Thread.self() を「field 1=unit のブロック」前提に検査し、TLS slot 配列を field 1 に Obj.magic 格納する。
systhreads stub の `Thread.t=int`(即値)だと `Obj.field (int) 1` が wasm `unreachable` trap。
**解: threads stub の Thread.t を `{_id:int; mutable _tls:Obj.t; _other:Obj.t}` の 3 フィールド
record にし、self() が安定インスタンスを返す**(`patches/threads-thread.ml`)。`.mli` の `type t` は
abstract のまま=外部表現不変で .cmi digest 一致(Haxe 側の再コンパイル不要)。

## 再現手順(検証ループ)

```bash
# A) コンテナで bytecode 再リンク(dune は外部 lib の overlay を拾わないので rm 必須)+ wasm 化
docker exec haxe-spike bash -lc '
  eval $(opam env); export PATH=/usr/local/bin:$PATH
  cd /home/opam/haxe
  rm -f _build/default/src/haxe.bc
  dune build --profile release src/haxe.bc
  rm -rf /repo/haxe-wasm/build/wasm; mkdir -p /repo/haxe-wasm/build/wasm
  wasm_of_ocaml compile --effects=cps _build/default/src/haxe.bc -o /repo/haxe-wasm/build/wasm/haxe.js'

# B) Node で byte 一致確認
bash haxe-wasm/harness/iter.sh                # → WASM == NATIVE GOLDEN: IDENTICAL ✓

# C) 実ブラウザ(WasmGC)で byte 一致確認
node haxe-wasm/harness/browser/run.mjs        # → ★ BROWSER WASM == NATIVE GOLDEN: IDENTICAL ✓

# D) Dockerfile から end-to-end 再現(クリーン clone + パッチ + 全ビルド + 検証)
bash haxe-wasm/build.sh                       # → ★ verify OK
```

native golden: `haxe-wasm/build/00_hello.native.raw.lua`
(sha256 `d5c975ec27a761e07aba06b6e05f616a713984863e5049636f51ed3111db5633`)。
無ければ `scripts/01_build_native.sh` で再生成。デバッグは `harness/trace_env.mjs` /
`wasm_of_ocaml compile --debug-info --sourcemap` + sourcemap 解析。

## 成果物のメンテ要点(再発防止)

- **dune が opam reinstall/overlay を拾わない**: lib を pin/reinstall・overlay 後は
  `rm -f _build/default/src/haxe.bc` してから `dune build`。
- threads stub を変えたら `scripts/patch_threads.sh` を再実行 → bc を rm → 再ビルド。
- Dockerfile の opam install は **`--no-depexts`**(conf-binaryen は手動 binaryen130、conf-neko は
  apt の neko/neko-dev で満たす)。binaryen は apt 108 不可(`wasm-merge` 欠落)→ release 130。
- **libmbedtls-dev / neko / neko-dev** は apt で導入(opam depext が拾わない / system 依存)。
- ctypes のポインタ返却関数を env で 0 ダミーにすると wasm illegal cast(boxed)→ pin で pure-OCaml 化。

## この先(製品化 = byte一致の先)

spike(実現性検証 + 成果物 + byte一致)は完了。**web playground への統合も完了**(2026-06-02)。

### web playground 統合(✅ 完了 — `web/playground/` + `web/README.md`)
`plan.md` の §共通 に沿って、**`.hx` 編集 → client-only wasm コンパイル → player ホットリロード**を実装:
- `web/scripts/gen-haxe-assets.mjs`(`npm run gen-haxe`): spike の `haxe.js`(patched glue)+
  `code-*.wasm` + Haxe std + lub externs + prelude を `web/public/haxe-wasm/` に固める(~18MB、gitignore)。
- `web/playground/haxe-compiler.{ts,worker.ts}`: 未改変 glue を Web Worker 内で Node 擬装 + in-memory
  VFS で動かす(`harness/browser/` と同方式)。`compileHaxe(files, mainClass)` が native の
  `haxe_build.c` と同じ連結(`HAXE_PRELUDE + raw + "\nreturn <Main>\n"`)で player 用 `.lua` を返す。
  WebAssembly.Module は 1 回だけコンパイルしてキャッシュ(compile ごと fresh instance)。
- `web/playground/{samples,main,editor}.ts`: `.hx`/`.hxml` ロード、boot/切替で compile→player 起動、
  編集 debounce→再 compile→`syncFiles`、editor に haxe シンタックス。
- 検証: `cd web && npm run verify`(headless Chromium / WebGPU swiftshader)が全 PASS。
  **`.hx` の clear_color を編集 → 再 compile → 背景が赤に**(A3)、全 11 サンプルが compile+描画(A5)。
  **Haxe 5 生成 lua が player で正しく動く**ことを確認(コミット済 `.lub/*.lua` は 4.3.7 生成で
  byte は異なるが、新方式は source から都度 compile するので無関係)。

### 残(任意)
- 最終採用時は native 側も Haxe5 に揃え二重バージョン drift を回避(現状 `.lub/*.lua` は 4.3.7)。
- 初回コンパイルは std 8.5MB bundle decode + wasm 9.5MB compile で重い。lazy-load / バンドル圧縮の最適化余地。

## 参照

- 調査全体と GO 根拠: [README.md](README.md)
- ブラウザ実機ハーネス: [harness/browser/README.md](harness/browser/README.md)
- 記憶(プロジェクト横断): memory `project_wasm_spike_go`
