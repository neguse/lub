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
└── mygame/
    ├── CMakeLists.txt    # add_subdirectory(../lub lub_build)
    ├── Game.cs
    ├── Game.csproj
    └── data/
        └── cube.slang
```

ゲームの CMakeLists.txt は lub のビルドだけを担当する。C# → Lua の transpile は lub が実行時に行う(dotnet SDK が要る)。

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
  │   GET /events        → SSE ストリーム
  │   POST /...          → (将来) プロファイル等
  │
  ├─ tcs pipeline (src/tcs_build.c)
  │   tcs を watch 起動: .cs 監視 + transpile → .lub/Game.lua
  │
  └─ file_watch
      ディレクトリ全体を監視 (.lub/*.lua, .slang, data/ 内全部。.cs は tcs が担当)
```

## ファイル同期プロトコル (SSE)

SSE エンドポイント `/events` で `EventSource` を使う。

### メッセージ形式

初回接続時も更新時も同じ形式。初回は全ファイル、更新時は変更分のみ。

```
event: files
data: {"files":{"game.lua":"<content>","data/cube.slang":"<content>"}}
```

### ブラウザ側

```js
const es = new EventSource('/events');
es.addEventListener('files', (e) => {
  const {files} = JSON.parse(e.data);
  for (const [path, content] of Object.entries(files)) {
    // パスは entry ディレクトリからの相対。常に samples/<entry>/ 配下へ書く。
    // 親ディレクトリを FS.mkdir で作り、既存ファイルは unlink してから書く
    writeFileEnsureDir(FS, 'samples/' + ENTRY + '/' + path, content);
  }
});
```

lub WASM の C 側が mtime ポーリングで変更を検知し、リロードする。

### 変更検知フロー

1. ファイル監視がディレクトリ全体の mtime を 50ms debounce でチェック
2. .cs の変更は tcs の watch が transpile し、生成された .lub/Game.lua の更新をファイル監視が拾う
3. .slang, data/ 等は変更されたファイルの中身をそのまま含める
4. 変更ファイル群を 1 メッセージで SSE 送信

## HTTP サーバー実装

POSIX ソケットで最小限の HTTP を実装する。

配信するもの:
- `/` → 埋め込み HTML (全画面 canvas + EventSource JS + lub.wasm ローダー)
- `/host.js` → ゲームディレクトリの `host.js` (無ければ空スクリプト)
- `/wasm/*` → lub の WASM ビルド成果物 (lub.js, lub.wasm, lub.data)
- `/events` → SSE

## ホストブリッジ (host.js + lub.Host)

ゲームディレクトリに `host.js` を置くと、serve ページが WASM 起動前に
同期ロードする。`host.js` は `window.lubHost` を定義し、ゲーム Lua とは
`lub.Host` (`host_available` / `host_send` / `host_poll`) で topic + payload
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
  data_watch_tick()          # .lub/*.lua, .slang, data/ 等の監視
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
├── host/Program.cs      # .NET 実行の入口 (Lub.Run(typeof(Game), args))
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

```bash
# lub Game.csproj が書き出した .lub/Game.lua (transpile 結果) と
# lub WASM 成果物 + data/ + index.html を配置
```

実装は `src/serve.c` (HTTP + SSE) と `src/embedded_serve_page.h` (埋め込み HTML/JS)。
