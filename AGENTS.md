# AGENTS.md

lub はコード中心のゲームエンジン。C# (TinyC#) で game を書き、tcs VM の bytecode にして hot reload で動かす。
native (DX12 / Vulkan / Metal 直接実装 + SDL3 GPU) と web (WASM + WebGPU) の両方で動く。

エージェントへの指示はこの file (と `tcs/AGENTS.md`) に置き、`CLAUDE.md` は置かない。Claude Code は、作業のディレクトリとその上に `CLAUDE.md` が無いときに `AGENTS.md` を読む。

## 現状の source of truth

memory に状態を溜めない。現在地は常に以下を読む:

- GitHub の issue — これからやること。やったことは PR の本文に書く(リポの中に作業の一覧や完了の記録は置かない)
- `README.md` — build、実行、整形、サンプルの置き方、runtime テストと golden
- `docs/design.md` — 設計方針
- `docs/api-glue.md` — API 面 (C# stub、C、tcs VM、.NET) の binding の構成と実装ライブラリの供給方針
- `docs/manual/` — ゲームを書く人向けのガイド(C# の書き方、lub が tcs を探す順、.NET 実行は `08-csharp.md`)
- `web/README.md` — web のサイトの build と headless の検査
- `.github/workflows/ci.yml` — CI の job と走り分け(各 job の先頭のコメント)
- `templates/game/` — 外部リポからゲームを書くテンプレート(C# の csproj。tcs VM の player と .NET 実行)
- `tcs/AGENTS.md` — `tcs/`(C# の compiler と tcs VM)を触るときの、tcs に固有の指示
- `git log` — 実装経緯

## 流儀

- PR ベースで回す。作業はブランチ → PR。マージ gate は PR CI で、required check は集約 job `gate` のみ。push は必ずユーザー承認後、merge は人間の act。
- 同じ規則や事実を 2 か所に書かない。code は正の実装を呼び、文書は正の文書を指す。言語の境目をまたぐなどで写すしかないときは、一致を確かめる検査を同じ PR に入れる。
- 検証: commit フック (`scripts/pre-commit.sh`) は format、空白、docs lint だけを走らせ、フル検証は PR CI が担う。CI を待たずに手元で確かめるときは `scripts/pre-push.sh`(何を走らせるかは script の先頭のコメント)。
- build は platform の script (`docs/release-build.md`) で行い、CMake の command を手で組まない。sandbox が build を止めたら、同じ script を走らせる許可を求める。
- 移植は理想設計で。原典 (NGS 等) のコード構造・ファイル分割・抽象化は真似しない。C# + lub 哲学から導いた最良案で書く。忠実に写すのは gameplay rule (敵パターン・弾数・HP・速度・出現タイミング) だけ。ファイル分割は概念単位 (Scene, EntityWorld, Atlas, Font, DrawList)、state machine は interface/enum/class、entity は List/Pool。
- ドキュメントは `docs/README.md` の方針に従う。live/record のディレクトリ分離、平易な語(jargon・完了注記・bold 禁止)、記録本文は当時のまま。機械検査は docs-lint。
- 整形とコメントの書き方は `README.md` の「コードフォーマット」に従う。

## サンプルとテスト

- サンプルの置き方は `README.md` の「実行」、C# の書き方と csproj の何を読むかは `docs/manual/08-csharp.md`。
- サンプルは実行例であってテスト対象ではない。サンプルに自己テストを書かず、samples 専用のテストも置かない。検査は `tests/cs/` の runtime テストと golden に限る(runtime に検証段が入るまでの方針)。
- test は C# で書き、tcs を通して動かす。tcs の出力と runtime の間の層に test を書かない。書き方は `README.md` の「runtime テスト」。
- API の記述は `cs-lib/lub_stub.cs` で、そこから作る生成物(一覧は `docs/api-glue.md` の接点ファイルの表の `tools/lub-gen`)は手で編集しない。stub を変えたら `scripts/gen-api.sh` で作り直す。

## web

- build、検査、サイトの page の構成は `web/README.md`。
- emcc は PATH に無くても、`source ~/emsdk/emsdk_env.sh` で使えることがある。`which emcc` だけで無いと決めない。
