# Phase 0 spike: Haxe compiler を WASM 化して web playground で client-only compile できるか

> 記録: 2026-05 時点の spike 計画。結果は GO で web playground 統合まで完了済み。
> 現状は [README.md](README.md)(実施記録)と [HANDOFF.md](HANDOFF.md)(引き継ぎ)を参照。

## Context

web playground (`web/`) で `.hx` を編集して動かしたい。コンパイル実行場所として
**(A) サーバ側 Docker**(公式 try.haxe.org と同方式、低コスト・実証済み)と
**(B) client-only WASM**(haxe compiler = OCaml を wasm_of_ocaml でブラウザ実行、backend 不要・
完全静的・オフライン可)の二択がある。

OCaml→WASM 自体は 2025-02 の **wasm_of_ocaml** 正式リリースで production-ready(WasmGC、OCaml5、
js_of_ocaml ドロップイン)。OCaml 製コンパイラのブラウザ実行は OCaml 公式 playground 等で実証済み。
よって (B) は「言語の壁」ではなく **haxe compiler の native 依存(C スタブ・OCaml5 マルチコア)の壁**が
問題。既存の haxe-in-wasm ビルドは存在せず自前保守になるため、本実装の前に **bounded な feasibility
spike** を先に1本通し、結果を見て (A)/(B) を最終決定する(ユーザ選択: 「まず WASM 検証だけ」)。

## この spike を de-risk する事実(repo 調査結果)

- サンプル・externs(`haxe-lib/lub/` の5ファイル)に **`macro`/`@:build`/`import haxe.macro`/`EReg`/
  regex リテラルは皆無**。externs は純粋な `@:native`/`@:luaRequire`/`@:multiReturn` のみ。
  → **eval(マクロ interp, luv/libuv)と pcre2(regex)の C スタブは通常 compile のホットパスに乗らない**。
- compile フラグ面は極小: `haxe -cp samples/<n> -lib lub -main <Class> --lua <out>`(`--connect` は
  常駐 server 用で one-shot では不要)。defines 無し。`src/haxe_build.c:266`。
- prelude/postlude は**純 Lua**(`src/embedded_prelude.h`、68行)+ 末尾 `"\nreturn <Class>\n"`。
  C 側の絡みなし。
- 最小サンプル: `samples/00_hello/`(`Hello00.hx` 814B + `00_hello.hxml` のみ、data 無し)。spike の
  ゴールデン入力に最適。
- **採用バージョン = Haxe 5(`5.0.0-preview.1`)**。ユーザ希望。repo は現状 README で「4.3+」、
  コミット済み `.lub/*.lua` は 4.3 生成。Haxe 5 は lua codegen が微変しうるため、**ゴールデンは
  Haxe 5 native で取り直す**(prelude shim = bit32/utf8/atan2 の調整要否もここで判明)。preview は
  非安定だが spike 用途では許容。最終採用時は native 側も Haxe 5 に揃えて二重バージョン drift を避ける。

## WASM 化の壁(`haxe.opam` の依存より)

| 依存 | 種別 | spike での扱い |
|---|---|---|
| `domainslib` / `saturn` / `thread-local-storage` / `dynamic_gc` | **OCaml5 マルチコア/domain** | **Haxe 5 では並列は opt-in(`-D enable-parallelism`)**。付けなければ単一 domain 実行 → js/wasm_of_ocaml 互換性が大幅に上がる。起動時に domain primitive を呼ばないかだけ確認 |
| `conf-libpcre2-8` | PCRE2 (C) | サンプルは regex 不使用 → 呼ばれなければ無視可。呼ばれたら JS `RegExp` shim |
| `luv` (libuv, C) | async/eval/server | one-shot `--lua` では event loop 不要 → 呼ばれた primitive のみ stub |
| `conf-neko` | Neko VM (C) | lua 生成では不要想定 → stub/未実装で可かを確認 |
| `conf-zlib` | zlib (C) | 圧縮 IO 用。lua 生成パスで呼ばれるか確認、呼ばれれば pako 等で shim |
| `sha` | SHA (C stub) | 軽微。呼ばれれば JS 実装 |
| Unix / file IO | OCaml stdlib | std ライブラリ・externs 読込 + 出力書込 → js_of_ocaml 疑似FS or WASI preopen |

ポイント: js/wasm_of_ocaml は**未実装 C primitive があってもビルドは通り、実際に呼ばれた時のみ失敗**
(コンパイル時に missing primitive 一覧が warning で出る)。よって spike は「最小 compile を走らせて
**実際に呼ばれる primitive だけ**を洗い出し、それだけ shim する」戦略が取れる。

## Phase 0: spike 手順(隔離環境で実施、repo へは `haxe-wasm/` 追加のみ)

1. **baseline**: `HaxeFoundation/haxe`(`5.0.0-preview.1` tag)を clone、OCaml 5 + opam 依存を入れ、
   **native haxe 5 をビルド**して `haxe -cp samples/00_hello -lib lub -main Hello00 --lua out.raw`
   (`-D enable-parallelism` は付けない=単一 domain)が通ることを確認。prelude+postlude 連結後の
   `.lua` を **Haxe 5 native 出力としてゴールデン化**(4.3 コミット済み `.lua` とは差分が出る可能性あり、
   その場合 prelude shim を Haxe5 lua codegen に合わせ調整)。lub externs が Haxe 5 で compile 通るかも
   ここで確認。
2. **bytecode ビルド**: haxe の dune を `(modes byte exe)` 等に調整し `dune build` で **`haxe.bc`** を
   生成(community 既出の手法。リンク問題を潰す)。`haxe.bc` が native と同じ出力を出すこと確認。
3. **wasm_of_ocaml 適用**: `wasm_of_ocaml compile haxe.bc -o haxe.wasm`(+ JS glue)。出る
   **missing primitive 一覧**を記録(= 呼ばれうる C スタブの全体像)。
4. **仮想FS + 実行ハーネス**: haxe std ライブラリ + `haxe-lib/lub/` externs + `samples/00_hello` を
   js_of_ocaml 疑似FS(または WASI preopen)に載せ、`HAXE_STD_PATH` を設定。**まず Node + wasm**で
   `haxe ... --lua out.raw` を実行。
5. **壁を順に潰す**: 実際に呼ばれた primitive のみ shim(FS、必要なら pcre2→RegExp 等)。
   **domain/マルチコアが単一 domain で動くか**をここで判定(最大の go/no-go ポイント)。
6. **最小 .hx→.lua 成功**: `00_hello` の `.lua` が native ゴールデンと**バイト一致**することを確認。
   続けて実ブラウザ(WasmGC: Chrome/Firefox/Safari)で同じく成功するか確認。
7. **コスト計測**: 配信バンドルサイズ(haxe.wasm + std lib 同梱)と cold/warm compile レイテンシ。

## Go / No-Go ゲート

- **GO → client WASM 採用**: ブラウザで `00_hello.hx → .lua` がゴールデン一致で生成でき、必要 shim が
  限定的(FS + 少数 primitive + 単一 domain で動作)、バンドル/レイテンシが許容範囲。
- **NO-GO → サーバ側 Docker へ**: 次のいずれか — domain/マルチコアが jsoo 下で深い patch 無しに動かない、
  呼ばれる C primitive が多すぎて shim が非現実的、バンドル過大 or レイテンシ過大。

## spike の成果物 / 検証

- `haxe-wasm/README.md`: missing primitive 一覧、実際に呼ばれた primitive、domain 判定、shim 量、バンドル
  サイズ、レイテンシ、**GO/NO-GO 勧告**。
- `haxe-wasm/`: ①haxe bytecode ビルド手順スクリプト ②wasm_of_ocaml 実行スクリプト ③`00_hello` を
  compile する Node-wasm ハーネス(出力を native ゴールデンと diff)。
- 検証 = ハーネスが `00_hello.hx → .lua` を native 一致で吐けば spike 成功。ブラウザ実行まで通れば GO 確度大。
- timebox 目安 1〜3日。深掘り前に手順3のmissing primitive一覧と手順5のdomain判定で早期に見切る。

## 共通(GO/NO-GO どちらでも使う web client 配線)

compile backend が wasm モジュールでも HTTP endpoint でも、**client 側の配線は共通**なので spike 結果は
そのまま差し込める:
- `web/playground/editor.ts`: `.hx`/`.hxml` に haxe シンタックス(`@codemirror/legacy-modes`)。
- `web/playground/samples.ts`: 生成 `.lua` ではなく `.hx`/`.hxml`/data をエディタにロード(列挙は
  sample manifest を dev plugin 動的生成 + build 時静的出力)。
- `web/playground/main.ts`: `.hx`/`.hxml` 編集を debounce → `compileHaxe()`(GO=wasm 呼出 / NO-GO=
  `POST /compile`)→ 成功なら `syncFiles {"<name>/.lub/<name>.lua": lua}` を iframe へ。`.slang`/
  `.verts.lua` は従来通り直接 sync。boot 時も一度 compile して `.lua` を MEMFS へ注入。
- `web/playground/player.ts`: 既存の MEMFS 書込 + C 側 mtime poll hotswap をそのまま流用。
- prelude/postlude 連結ロジックは native(`src/embedded_prelude.h` 抽出 + hxml の `-main` 解析)を
  共有モジュール化し client から再利用。

## Fallback 設計(NO-GO 時のサーバ側、参考)

公式 try.haxe.org と同方式。`web/compile-server/`(Node + `haxe --wait` 常駐 + 上記共有モジュール)を
Docker 化(**Haxe 5 preview**。公式 `haxe:5` イメージが無ければ preview バイナリ/ソースから自前 image)。
前段に薄いルータ(Cloudflare **Worker** が `/compile` を **Container** に proxy、他は静的 assets)。
haxe はエッジ Worker ではなく Container で走る。CF Containers(2026-04 GA)or 任意 Docker ホスト
(Fly.io 等、`VITE_HAXE_COMPILE_URL` で振替、要 CORS)。サーバ側なら domain/マルチコアの WASM 制約は
無関係なので `-D enable-parallelism` も自由に使える。

