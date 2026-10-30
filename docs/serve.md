# lub --serve

lub を外部リポのゲームプロジェクトから使うための Web 開発モード。
ネイティブの `lub Game.csproj` と対称的に、Web ブラウザをレンダリング先としてホットリロード開発を行う。

## 動機

lub のゲームコンテンツを別リポで管理したい。ネイティブ開発は `lub Game.csproj` で完結するが、Web では WASM 成果物の配信とファイル同期の仕組みが必要。

## ディレクトリ構成

ゲームリポと lub リポは兄弟ディレクトリに置く。

```
~/projects/
├── lub/
└── mygame/               # templates/game を写したもの
    ├── Game.cs
    ├── Game.csproj
    ├── host/Program.cs
    └── data/
        └── cube.vs.slang
```

ゲームのリポは lub をビルドしない。lub リポでビルドした `lub` を使い、C# → Lua の transpile は lub が実行時に行う(dotnet SDK が要る)。

lub は実行ファイルの 1 階層上の `third_party/tcs` と `cs-lib` を探す(cwd 直下も見る)。
別の配置にするときは、環境変数 `LUB_TCS`(tcs の起動コマンド。例: `dotnet run --project <lub>/third_party/tcs/Transpiler/Transpiler.csproj --`)と
`LUB_CS_LIB`(`cs-lib` ディレクトリ)で指す。

## 使い方

```bash
# ネイティブ開発
./build/lub mygame/Game.csproj

# Web 開発
./build/lub --serve mygame/Game.csproj
# → http://localhost:8080 で配信開始 (--port N で変更)
# → ブラウザで開く → ゲーム全画面表示
# → .cs や .slang を編集 → 自動リロード
```

WASM 成果物 (`build/wasm/lub.{js,wasm,data}`) と slang-wasm (`web/public/slang/`) は
実行ファイルの位置から自動検出される。別の場所に置く場合は `--wasm-dir` /
`--slang-dir` で指定する。

## アーキテクチャ

```
lub --serve Game.csproj
  │
  ├─ HTTP server (localhost:PORT)
  │   GET /              → HTML (全画面 canvas + SSE クライアント JS)
  │   GET /wasm/lub.*    → WASM 成果物 (lub.js, lub.wasm, lub.data)
  │   GET /slang/*       → slang-wasm
  │   GET /files/<path>  → ゲームのファイル (中身そのまま)
  │   GET /events        → SSE ストリーム
  │   POST /...          → (将来) プロファイル等
  │
  ├─ tcs pipeline (src/tcs_build.c)
  │   tcs を watch 起動: .cs 監視 + transpile → .lub/Game.lua
  │   以後の build ごとの reload chunk を tcs の標準出力から受ける
  │
  └─ file_watch
      ディレクトリ全体を監視 (起動後に足したファイルと .slang, data/, .lub/ の下を含む。.cs / .csproj、生成 Lua、. で始まる名前は除く。.cs は tcs が担当)
```

## ファイル同期プロトコル (SSE)

SSE エンドポイント `/events` で `EventSource` を使う。

### ファイルの置き場所

ブラウザの MEMFS の `/` を、`lub --serve` を起動した cwd とみなす。
ゲームのファイルは cwd からの相対 path に置くので、ゲームは native と同じ path で読める(`templates/game` で起動すれば `data/cube.vs.slang`、リポジトリ root から `samples/03_texture/Texture03.csproj` を渡せば `samples/03_texture/data/...`)。
cwd の外にあるゲームは、ゲームのディレクトリを `/` に置く。
起動の entry は生成 Lua の path(`.lub/Game.lua` など)。

### メッセージ形式

`files` イベントはファイルの名前(MEMFS での path)だけを知らせ、中身はブラウザが `GET /files/<path>` で取る。
中身をそのまま返すので binary も壊れず、大きさの上限も無い。
`/files/` が返すのは `files` で知らせた名前だけで、それ以外は 404。

初回接続時も更新時も同じ形式。初回は全ファイル。

```
event: files
data: {"files":[".lub/Game.lua","data/cube.vs.slang","data/cube.verts.lua"]}
```

### ブラウザ側

runtime の `Io` は MEMFS にあるファイルだけを読み、自分では取りに行かない。
そこでページは初回の `files` の全ファイルを取ってから WASM を起動し、main() の前に MEMFS へ書く。
更新の `files` は届いた順に取り、取り終えたものを MEMFS に書く(親ディレクトリを作り、既存ファイルは unlink してから書く)。
lub WASM の C 側が mtime ポーリングで変更を検知し、リロードする。

C# の変更は、tcs の reload chunk (前の build からの差分) を 1 つずつ別のイベントで送る。
data は chunk の Lua を JSON 文字列にしたもの。
ページは chunk を MEMFS に書いて `lub_queue_reload_chunk_file` に渡し、runtime が届いた順に実行中の VM へ当てる。
runtime が起動する前に届いた chunk は、ページが溜めて起動後に順に渡す。
接続が切れて再接続したときは、切れている間の chunk が届いていないので、ページを読み直して最新の出力から起動し直す。
reload chunk で更新する entry (`.lub/Game.lua`) は、`files` で届いても読み直さない。

```
event: reload
data: "<reload chunk の Lua>"
```

chunk で移行できない変更では、tcs が reload chunk の代わりに restart を知らせる。
その build の出力は書き終わっているので、ページは理由を console と status に出して読み直し、最新の出力から起動し直す。
runtime が VM を失ったとき(出力を読み込めなかった、reload chunk の適用が失敗した)も、runtime が `window.lubVmLost` でページに知らせ、ページは最新の出力から起動し直す。
chunk の失敗は出力が既に新しいので今すぐ読み直し、出力そのものが読み込めなかったときは次の `reload` か `restart` が届いてから読み直す。
native の `lub Game.csproj` は同じ状況で出力から Lua VM を作り直す。
data は改行終端の理由の行を JSON 文字列にしたもの。

```
event: restart
data: "<理由の行>"
```

### 変更検知フロー

1. ファイル監視がディレクトリ全体を 250ms ごとに走査し、変更を見つけたら 50ms ごとに走査して変更が止まるのを待つ
2. .cs の変更は tcs の watch が transpile し、reload chunk を `reload` イベントで送る。生成された .lub/Game.lua は .lub/ の下でただ 1 つファイル監視の外で、ページを読み直したときに初回の `files` に入り、その時点の中身が初期状態になる
3. .slang, data/ 等はファイルの名前を `files` イベントで送り、ブラウザが `/files/` で中身を取る。起動後に足したファイルも同じで、消したファイルは監視から外れ、ブラウザの MEMFS には残る
4. ファイル監視は変更のあった回に監視中の全ファイルの名前を 1 メッセージで送る

## HTTP サーバー実装

POSIX ソケット(Windows では `src/sock_compat.h` が Winsock に写す)で最小限の HTTP を実装する。

配信するもの:
- `/` → 埋め込み HTML (全画面 canvas + EventSource JS + lub.wasm ローダー)
- `/host.js` → ゲームディレクトリの `host.js` (無ければ空スクリプト)
- `/wasm/*` → lub の WASM ビルド成果物 (lub.js, lub.wasm, lub.data)
- `/slang/*` → slang-wasm (`--slang-dir`)
- `/files/*` → ゲームのファイル (`files` イベントで知らせたものだけ)
- `/events` → SSE

## ホストブリッジ (host.js + Host)

ゲームディレクトリに `host.js` を置くと、serve ページが WASM 起動前に
同期ロードする。`host.js` は `window.lubHost` を定義し、ゲームとは
`Host` (`Host.Available` / `Host.Send` / `Host.Poll`) で topic + payload
(バイナリ可) を交換する。ネットワーク (WebTransport 等) や clipboard の
実体はホストページ JS 側に置く。契約の詳細は `src/host.c` 冒頭を参照。

```js
// host.js の骨格
window.lubHost = {
  queue: [], // ゲームへの受信キュー: {topic, payload: Uint8Array|string}
  onMessage(topic, payload /* Uint8Array */) { /* ゲームからの送信 */ },
};
```

## メインループ

`--serve` モードではウィンドウを作らない。SDL は初期化するがヘッドレスで動作する。

```
while running:
  accept new HTTP connections
  handle HTTP requests
  data_watch_tick()          # .slang, data/ 等の監視
                             # (.cs → .lub/Game.lua は tcs の watch プロセスが書く)
  if changes:
    send SSE to all connected clients
  sleep or poll
```

## template-game

lub リポ内の `templates/game/` がテンプレート。C# のゲームが 2 つの実行形で
動き、`--serve` にも同じ csproj を渡す。

```
templates/game/
├── Game.csproj          # entry 指定 (lub) 兼 .NET 実行の project (dotnet)
├── Game.cs              # 立方体フラッピーバード (3D)
├── host/Program.cs      # .NET 実行の入口 (App.Run(typeof(Game), args))
└── data/
    ├── cube.vs.slang    # 最小 3D シェーダー (MVP + 単色)
    ├── cube.fs.slang
    └── cube.verts.lua
```

```sh
cd templates/game
../../build-release-linux/lub Game.csproj           # tcs→Lua (transpile + watch + hot reload)
../../build-release-linux/lub --serve Game.csproj   # 同じものをブラウザで
dotnet run                                          # .NET 実行 (共有 library を P/Invoke)
```

`cp -r lub/templates/game ../mygame` でコピーして使い始める。コピー先では
`Game.csproj` の `LubRoot` (lub の checkout) と `LubNativeDir` (共有 library の
場所) を合わせる。

## Web デプロイ

開発時は `--serve` でホットリロード。デプロイ時は静的ファイルとして配る。
`lub export web Game.csproj -o out` が、player とゲームの snapshot を全部埋め込んだ `out/index.html` を 1 枚書く(`docs/manual/03-lifecycle.md`、`docs/release-kit.md`)。

実装は `src/serve.c` (HTTP + SSE) と `src/embedded_serve_page.h` (埋め込み HTML/JS)。
