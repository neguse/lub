# パッケージング構想 — 配信は最後の hot reload

> 記録: 2026-07-23 時点の設計議論の到達点。roadmap を「自作ゲーム 4 本の移植と
> win/web 配信」駆動に切り替えた(PR #15)ことを受けて、配信パッケージの作り方を
> 議論した。ここに書くのは方針の合意と、まだ案の段階のもの。実装はしていない。

## 前提にした調査

- love2d: ゲームは「main.lua がルートの zip(.love)」。win 配布は
  `copy /b love.exe+game.love game.exe` のバイナリ連結 + 同梱 DLL。
  web は公式でなくコミュニティの love.js。公式の 1 コマンド export が
  無いため、makelove / love-release 等のコミュニティツールが乱立している。
- usagi (brettchalupa): Rust + Lua 5.5 + live reload の 2D エンジン。
  `usagi export` 1 コマンドで各プラットフォームの zip と携帯用バンドルを生成。
  fuse は「exe 末尾に bundle を追記し magic footer が指す」形式。
  クロスプラットフォームは release ごとに公開される事前ビルド済み
  runtime template を CLI が取得・キャッシュ・sha256 検証。ホスト向けは
  実行中の自分のバイナリに fuse するのでオフラインで完結する。
  ユーザにコンパイラやビルドシステムは要らない。

ただしどちらも「静的データを固めて配る」エンジンであり、そのまま
なぞらない。lub の勝ち筋はアセットをランタイムが生成できることにある。

## 合意した方針

### cook を作らない

事前にしか使えない変換パイプライン(cook)には投資しない。ランタイムが
今日すでにやっている生成(Haxe transpile、Slang compile、アトラス構築など)
と別実装の事前変換を持つと二重実装になり、ランタイム生成という強みを
自分で削ることになる。

代わりに冪等キャッシュを使う。ランタイム自身の決定的な生成物を
「入力のハッシュ + 生成器のバージョン」をキーに保存したもので、
export 時に温めて同梱する。player は cache hit を読み、miss なら
生成器が同梱されていれば同じコードで再生成する。cache は純粋な最適化で
あって、必須の変換工程ではない。luac バイトコード化のような起動高速化も
cook でなく cache entry として表現できる。決定的でない生成
(math.random が絡むもの等)は cache 対象外としてランタイム専属に残す。
この線引きが規律になる。

前例は既にある: `web/tcs-prebuilt/`(cold 起動 0.5s の正体)と
`web/public/haxe-wasm/std-bundle.json` は冪等生成物のプリキャッシュである。
場当たりの仕組みから content-addressed cache という一級の概念に昇格させる。

### パッケージ = serve ペイロードの凍結

lub の開発は「Lua のリビジョンを生きた runtime に流し込み続けるストリーム」
(serve の snapshot 配信 → hotswap → commit ACK)。パッケージとは、その
ストリームの 1 リビジョンを切り出して、watcher・コンパイラ・dev server
なしで起動できるようにしたもの。ビルドではなく凍結。

- パッケージの中身 = serve が既に流している「entry Lua 一式 + assets」を
  self-contained に直列化したもの + 冪等キャッシュ。export は新しい
  パイプラインではなく、serve の送信物のディスクへの直列化。
- player = 購読をやめた runtime。同じバイナリが SSE でリビジョンを待つ
  代わりにファイルから凍結リビジョンを読む。
- 正しさの定義: パッケージを起動して golden と byte 一致すること。
  凍結がゲームを変えていないことを機械検査できる。
- zip の形(win の exe 同梱 zip、itch.io 用の index.html がルートの zip、
  fuse)は容れ物の話であり最外殻の些事。本体は snapshot の仕様
  (何を凍結すれば self-contained か)にある。

### デフォルト経路にビルドシステムを要求しない

パッケージ = serve ペイロードの凍結、の帰結として、パッケージには
コンパイルすべきものが存在しない。player バイナリはゲームに依存しない
(ゲームは Lua + assets というデータ)ので、player は lub のリリースとして
一度だけビルドされていればよく、ゲーム作者の export がやることは
ファイルの配置だけになる。

だから export に CMake や emsdk を要求してはいけない。要求した瞬間、
エンジンをビルドする都合(C toolchain、emsdk、プラットフォームごとの
環境)がゲーム作者全員に転嫁される。要求しなければ:

- export は lub が動く環境ならどこでも同じに動く。Linux 機で win
  パッケージが作れる。事前ビルド済み player を並べるだけなので、
  クロスコンパイルという概念自体が発生しない。
- CI のパッケージ工程は「export を 1 回呼ぶ」で終わる。
- 開発が `lub game.hxml` で始められる敷居の低さが、配信まで途切れない。

ビルドシステムが登場するのは player 自体を作り変える場合だけで、
それが次節の逃げ道層。

### 逃げ道としてのライブラリ化は必須

自前の main() を書き、任意のネイティブ関数を足したゲーム実行物を
作れること(lub を CMake のライブラリターゲットとしてリンクする)は
ゲームに必須。基本は使わないが、存在することが要件。web で任意の
ネイティブ拡張ができるのは「ユーザの exe ごと emscripten でビルドする」
この経路だけでもある。love2d はこの逃げ道を持たず、エンジンごと
再ビルドするしかないのが長年の不満点になっている。
この層のパッケージングは各プロジェクトの管轄で、lub は snapshot 仕様
だけ定義する。

## 案の段階のもの(未決)

- lub の正体を Lua contract と定める見方。core API、`lub_prelude.lua` が
  注入する namespace table、hotswap プロトコルが製品境界で、
  コンパイラ(Haxe / tcs)は周辺機器(別プロセス / wasm モジュール)。
  この見方を採るなら、いま暗黙にある contract を文書化された契約に
  昇格させる必要がある。
- contract を IDL で記述し、各言語の extern / stub / docs を生成する案。
  WebIDL が本命(WebGPU 仕様の実績、パーサが枯れている)。TS .d.ts が
  対抗馬で、generator を書く前に一度だけ比較する。Lua の多値返しや
  宣言型 API は lub 拡張属性で表現する。generic の扱いは要設計。
  いきなり生成せず、既存 API の書き起こし + 手書き実装との突き合わせ検査
  (docs-lint と同族)から入る。
- lubx は Lua 実装を正とし、C# 面(と残すなら Haxe extern)は IDL から
  生成する案。実装の二重化(`docs/api-glue.md` の現方針)が消える代わり、
  lubx 内部の開発が型検査を失う。検証は既存の C# サンプル golden と
  A5/A6 がそのまま使える。
- Haxe の処遇。extern 生成で維持費が下がる前提なら freeze
  (新機能投資の停止、動態保存)が本命に見えるが未決。
  ngs 製品化(Haxe 製)の言語選択が最初の踏み絵になる。

## 次の一歩

serve が配信している snapshot を手でディレクトリに保存し、native lub を
そこに向けて watch なしで起動できるか確かめる。「固定エントリ Lua を
watch なしで食う」経路の有無が snapshot 仕様の最初の設計材料になる。
