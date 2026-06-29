# lub --serve

lub を外部リポのゲームプロジェクトから使うための Web 開発モード。
ネイティブの `lub game.hxml` と対称的に、Web ブラウザをレンダリング先としてホットリロード開発を行う。

## 動機

lub のゲームコンテンツを別リポで管理したい。ネイティブ開発は `lub game.hxml` で完結するが、Web では WASM 成果物の配信とファイル同期の仕組みが必要。

## ディレクトリ構成

ゲームリポと lub リポは兄弟ディレクトリに置く。

```
~/projects/
├── lub/
└── mygame/
    ├── CMakeLists.txt    # add_subdirectory(../lub lub_build)
    ├── Main.hx
    ├── game.hxml
    └── data/
        └── cube.slang
```

ゲームの CMakeLists.txt は lub のビルドだけを担当する。Haxe コンパイルは lub が実行時に行う。

## 使い方

```bash
# ネイティブ開発 (今まで通り)
./build/lub mygame/game.hxml

# Web 開発
./build/lub --serve mygame/game.hxml
# → localhost:PORT で配信開始
# → ブラウザで開く → ゲーム全画面表示
# → .hx や .slang を編集 → 自動リロード
```

## アーキテクチャ

```
lub --serve game.hxml
  │
  ├─ HTTP server (localhost:PORT)
  │   GET /              → HTML (全画面 canvas + SSE クライアント JS)
  │   GET /wasm/lub.*    → WASM 成果物 (lub.js, lub.wasm, lub.data)
  │   GET /events        → SSE ストリーム
  │   POST /...          → (将来) プロファイル等
  │
  ├─ haxe_pipeline (既存)
  │   haxe --wait server + .hx 監視 + コンパイル
  │
  └─ file_watch (拡張)
      ディレクトリ全体を監視 (.hx, .slang, data/ 内全部)
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
    const full = path.startsWith('samples/') ? path : 'samples/' + path;
    FS.writeFile(full, content);
  }
});
```

lub WASM の C 側が mtime ポーリングで変更を検知し、リロードする。

### 変更検知フロー

1. ファイル監視がディレクトリ全体の mtime を 50ms debounce でチェック
2. .hx が変更された場合 → haxe_pipeline でコンパイル → 生成された .lua を含める
3. .slang, data/ 等は変更されたファイルの中身をそのまま含める
4. 変更ファイル群を 1 メッセージで SSE 送信

## HTTP サーバー実装

POSIX ソケットで最小限の HTTP を実装する。

配信するもの:
- `/` → 埋め込み HTML (全画面 canvas + EventSource JS + lub.wasm ローダー)
- `/wasm/*` → lub の WASM ビルド成果物 (lub.js, lub.wasm, lub.data)
- `/events` → SSE

## メインループ

`--serve` モードではウィンドウを作らない。SDL は初期化するがヘッドレスで動作する。

```
while running:
  accept new HTTP connections
  handle HTTP requests
  haxe_pipeline_tick()       # .hx 監視 + コンパイル (既存)
  file_watch_tick()          # data/ 等の監視 (拡張)
  if changes:
    send SSE to all connected clients
  sleep or poll
```

## template-game

lub リポ内に `templates/game/` としてテンプレートを用意する。

```
templates/game/
├── CMakeLists.txt       # add_subdirectory(../lub lub_build)
├── Main.hx              # 立方体フラッピーバード (3D)
├── game.hxml
└── data/
    └── cube.slang       # 最小 3D シェーダー (MVP + 単色)
```

`cp -r lub/templates/game ../mygame` でコピーして使い始める。

## Web デプロイ

開発時は `--serve` でホットリロード。デプロイ時は静的ファイルとして配る。

```bash
haxe game.hxml                    # .hx → .lua
# lub WASM 成果物 + game.lua + data/ + index.html を配置
```

## 実装順序

1. HTTP + SSE サーバー (`src/serve.c`)
2. ファイル監視の拡張 (.hx 以外も対象に)
3. `--serve` フラグ + ヘッドレスメインループ
4. ブラウザ側 HTML/JS (埋め込み)
5. template-game
