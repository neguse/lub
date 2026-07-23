# パッケージング構想 — 配信は最後の hot reload

> 記録: 2026-07-23 時点の設計議論の到達点。roadmap を「自作ゲーム 4 本の移植と
> win/web 配信」駆動に切り替えた(PR #15)ことを受けて、配信パッケージの作り方を
> 議論した。ここに書くのは方針の合意と、まだ案の段階のもの。実装はしていない。
> 現状の記述は 2026-07-23 時点のコードを読んで確認した挙動で、この文書
> 単体で読めるように書く。

## 現状の実体

構想の前に、今の lub が配信に対してどこまで来ているかの事実。

- 生成 .lua の直パス entry は既にある。`lub path/to/entry.lua` は staging
  なしで任意パスの .lua をロードし、監視は entry ファイルの mtime poll
  だけで Haxe や dev server を要求しない。csproj entry も transpile 後は
  同じ経路に合流する。
- boot と prelude は実ファイル。native は `samples/boot.lua` を cwd 優先、
  無ければ実行ファイル位置からの相対で探す。
  つまり runtime ライブラリの Lua(boot / lub_prelude / lub_io)は
  パッケージが同梱すべきファイル集合の一部で、binary には入っていない。
- serve が web に送っているものは具体的で、初回 SSE は
  「全ゲームデータファイル + 生成済み entry Lua」の中身を 1 つの JSON
  (相対パス → ファイル内容)として流す。HTTP 側はページ本体、
  wasm runtime(lub.js / lub.wasm)、slang-wasm、SSE 接続口を
  返すだけ。
- シェーダのディスクキャッシュは存在しない。native / web とも毎起動
  Slang でコンパイルする。パイプラインキャッシュはあるがメモリ内のみ。
- 冪等プリキャッシュの前例はあるが手動。`web/tcs-prebuilt/`(cold 起動
  0.5s の正体)と `web/public/haxe-wasm/std-bundle.json` は
  `npm run gen-tcs-prebuilt` / `gen-haxe` で人が再生成する。

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

### パッケージ = serve ペイロードの凍結

serve の初回 SSE が流すファイル集合(生成済み entry Lua + ゲームデータ)は、
「このリビジョンのゲームを動かすのに必要なファイルの閉包」を serve が
既に計算しているということ。パッケージとはこの閉包をディスクに書き出し、
更新が二度と来ない前提で起動できるようにしたもの。ビルドではなく凍結。

- web パッケージの実体 = 初回 SSE の files を静的ファイルに展開したもの
  + `/wasm/` 相当の runtime 資産 + 静的シェル(埋め込みページの購読なし版)。
  差分は「SSE を購読する代わりに同じファイルを URL から読む」ローダー。
- native パッケージの実体 = 同じファイル集合 + boot/prelude + 事前ビルド
  済み player。`.lua` 直パス entry が既にあるので、残る差分は
  「boot.lua の探索規約をパッケージ配置に合わせる」ことと
  「アセットの基準パス規約」程度。mtime poll は残っても実害がない。
- 正しさの定義: パッケージを起動して golden と byte 一致すること。
  凍結がゲームを変えていないことを機械検査できる。
- zip の形(win の exe 同梱 zip、itch.io 用の index.html がルートの zip、
  fuse)は容れ物の話であり最外殻の些事。本体は上の閉包の仕様にある。

### cook を作らない

事前にしか使えない変換パイプライン(cook)には投資しない。ランタイムが
今日すでにやっている生成(Haxe transpile、Slang compile、アトラス構築など)
と別実装の事前変換を持つと二重実装になり、ランタイム生成という強みを
自分で削ることになる。

代わりに冪等キャッシュを作る(現状には無い。これが新規実装の本体)。
ランタイム自身の決定的な生成物を「入力のハッシュ + 生成器のバージョン」を
キーに保存する content-addressed store で、player は hit を読み、miss なら
生成器が同梱されていれば同じコードで再生成する。cache は純粋な最適化で
あって、必須の変換工程ではない。luac バイトコード化のような起動高速化も
cook でなく cache entry として表現できる。決定的でない生成
(math.random が絡むもの等)は cache 対象外としてランタイム専属に残す。
この線引きが規律になる。

「開発中に cache が温まる」は現状の性質ではなく設計要件で、dev の実行時
コンパイル経路をこの store への write-through にすることを指す。手動 regen
している tcs-prebuilt / std-bundle をこの概念に取り込めるかは別途判断。

### 具体例: シェーダコンパイル

冪等キャッシュの主役はシェーダコンパイル。現状はディスクキャッシュが
無く毎起動コンパイルしている(前掲)。出力は入力に対して決定的なので
cache entry の条件を満たす。キーは「slang ソースのハッシュ、target、
entry とオプション、slang / DXC のバージョン」。SDL_GPU 向けの
binding 番号の振り直しのような後処理も生成器の一部としてキーに畳む。

生成器(Slang)を player に同梱するかは、教義ではなくターゲットごとの
費用対効果の選択で、アーキテクチャはどちらも許す。ここが cook との違い。

- native は同梱を続けるのが自然。今すでにリンクしていて、外す動機が
  バイナリサイズしかない。同梱しておくと、cache が cold でも起動する
  (miss ならその場で生成)し、ランタイムでシェーダソースを合成する
  遊びが残る(アセットのランタイム生成という勝ち筋の延長)。
- web は外すのが自然。slang-wasm の同梱は数 MB と起動コストを払うので、
  web player は生成器なしで shader cache が load-bearing になる。
  「web ではシェーダ集合が export 時に閉じる」制約を受け入れる。
  ランタイムシェーダ合成を web でやりたいゲームが出たら、そのゲーム
  だけ同梱する選択も残る。

export との交差点: export = runtime を 1 回走らせて cache を温める、
なので「Linux 上で DXIL を温められるか」という問題がある。DXIL は
DXC 依存で、DXC の Linux ビルドは存在するはずだが lub の現経路は
dxcompiler.dll 前提なので要検証。ただしここで cache-not-cook の利点が
効く: native player が生成器を同梱している限り、export 機が DXIL を
温められなくても win パッケージは正しく動く(初回起動が少し遅いだけ)。
温めの完成度は最適化の問題であって、正しさの問題にならない。cook 方式
だとここが「Linux からは win 向けを出せない」という機能欠損になる。

dev の挙動は変えない(今日と同じ実行時コンパイル + hot reload。cache が
できたら dev もそこへ write-through する)。

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
現状の lub は単一の実行物ターゲットでライブラリターゲットが無いので、
ここはライブラリ分割という実装を伴う。この層のパッケージングは各プロジェクトの
管轄で、lub は snapshot 仕様だけ定義する。

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

`.lua` 直パス entry と boot.lua 探索の現状(前掲)を使い、lub リポの外の
ディレクトリに「boot/prelude + 生成済み entry Lua + assets」を手で並べて、
事前ビルド済み lub がそれだけで起動するかを確かめる。通らない箇所
(boot 探索、package.path、アセット基準パス)がそのまま snapshot 仕様の
最初の項目になる。web 側は初回 SSE の files を静的展開した版で同じことを
やる(購読なしローダーの要否と形が判明する)。
