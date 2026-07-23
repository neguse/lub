# 言語アーキテクチャの案

> 記録: 2026-07-23。パッケージング設計(`2026-07-23-packaging-design.md`)の
> 議論から派生した、言語まわりの案。どれも未決で、決定ではない。

## lub の正体を Lua contract と定める見方

core API、`lub_prelude.lua` が注入する namespace table、hotswap
プロトコルが製品境界で、コンパイラ(Haxe / tcs)は周辺機器(別プロセス /
wasm モジュール)という整理。実態は既にそうなっている(native の Haxe は
コンパイルサーバ、web の Haxe と tcs は wasm モジュール)。この見方を
採るなら、いま実装の中に暗黙にある contract を文書化された契約に
昇格させる必要がある。

## contract を IDL で記述し、各言語の面を生成する案

- Haxe extern / C# stub / API docs はいま同じ契約を複数箇所に手書きして
  いる。contract を IDL 1 ファイルにすれば、これらが生成物に落ちる。
- 記述言語は WebIDL が本命(WebGPU 仕様での実績、パーサが枯れている)。
  TS の型定義ファイルが対抗馬で、generator を書く前に一度だけ比較する。
- Lua の多値返しや宣言型 API は WebIDL に無いので拡張属性で表現する。
  generic の扱いは要設計。
- いきなり生成に行かず、既存 API の書き起こしと手書き実装との突き合わせ
  検査(docs-lint と同族の機械検査)から入る。

## lubx を Lua 実装にする案

lubx の正を Lua 実装とし、C# の面(残すなら Haxe extern も)は IDL から
生成する。実装の二重化(`docs/api-glue.md` の現方針)が消える代わり、
lubx 内部の開発が型検査を失う。検証は既存の C# サンプル golden と
web 検証(A5 / A6)がそのまま使える。

## Haxe の処遇

extern 生成で維持費が下がる前提なら、freeze(新機能投資の停止、動態保存)
が本命に見えるが未決。維持費の実体は、lubx の Haxe / C# 二重実装、
全サンプル両言語対応、haxe-wasm(コンパイラの client-WASM 化)の維持と
std-bundle 再生成の運用。ngs は Haxe 製なので、ngs 製品化の言語選択が
この判断の最初の踏み絵になる。
