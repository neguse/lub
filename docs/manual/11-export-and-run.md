# 実行モードと配布

TinyC# で書いたゲームコードは、同じソースのまま 3 つの実行モードで動かせる。

## 3 つの実行モード

- `tcs VM` モード: バイトコード (`.tcsb`) にコンパイルしてプレイヤー上で動かす。`lub Game.csproj`・`lider`・Web 配布で使われ、開発時はホットリロードが効く。
- `tcs C` モード: `tcs c` で C コードへ変換し、`liblub` と静的リンクして単一のネイティブ実行ファイルにする。実行性能を重視する配布向け。
- `.NET` モード: `dotnet run` で実 `.NET` 上から共有ライブラリ `liblub` を呼ぶ。IDE の補完・デバッガの利用や、`tcs VM` との挙動比較に使える。

`.NET` モードで雛形プロジェクトを動かすときは、`Game.csproj` 内の `LubRoot` に lub アーカイブの展開先パスを指定する (既定では相対パス `../..` を参照する)。

## 配布物の書き出し (`lub export`)

`lub export` は GPU やウィンドウを使わずに `OnInit` → `OnExport` を 1 回実行し、対象プラットフォーム向けの配布スナップショットを書き出す:

```sh
lub export <platform> Game.csproj -o dist/
```

`<platform>` には `web`・`linux`・`windows`・`macos`・`ios`・`android` のいずれかを指定する (`windows` は DXIL 生成に `dxcompiler` が必要なため、Windows 版の `lub` で実行する)。

出力ディレクトリには次がまとめられる:

- ゲームのバイトコード (`<Entry>.tcsb`)
- `OnInit` / `OnExport` 中に `Io.Load*` で読み込んだファイル (同じ相対パスに複製。ゲームディレクトリ外のパスは同梱されない)
- 対象プラットフォームのグラフィックスバックエンド向けに事前コンパイルしたシェーダ (`cache/shader/`)

`OnExport` で宣言されなかったシェーダはキャッシュに入らないため、Slang コンパイラを持たないプレイヤー (Web 等) では実行時にエラーになる。

ネイティブ配布では、出力したスナップショットと同じディレクトリにプレイヤー (`lub` 実行ファイルと必要な共有ライブラリ) を置くと、引数なしで起動したときに隣の `.tcsb` を自動で実行する。

## Web 配布 (WASM + WebGPU)

Web プレイヤーは `liblub` と `tcs VM` を WASM にしたもの (`lub.js` / `lub.wasm`) で、WebGPU で描画する。ゲームの C# コードはネイティブと同じまま動く。

`lub export web` は、スナップショットと Web プレイヤーを圧縮して埋め込んだ 1 枚の `index.html` を出力する。外部ファイルを読めない環境でも単体で動作する。

独自の Web ページへ組み込む場合は、生成された `index.html` にスクリプトを書き足すか、起動スクリプトを自分のページへ写して使う。起動処理は WebGPU デバイスを作ってプレイヤーへ渡し、ゲームが読むファイルを MEMFS に配置してから `lub.js` を読み込む。

### 対応ブラウザ

WebGPU が有効なブラウザで動作する:

- Chrome / Edge: 137 以降 (既定で有効)
- Safari: iPadOS / iOS / macOS 26 以降
- Firefox Nightly: `about:config` で `dom.webgpu.enabled` を有効化

## Web ページの JS とやり取りする (`Host`)

ページ側で `window.lubHost` を定義しておくと、ゲームから `Host` API でトピックとペイロードをやり取りできる。通信 (WebTransport 等) やクリップボードのようにプレイヤーが持たない機能はページ側の JS に置く:

```js
window.lubHost = {
  // ゲームの Host.Poll が先頭から 1 件ずつ取り出す (payload は Uint8Array か string)
  queue: [],
  // ゲームの Host.Send が届く (payload は UTF-8 の Uint8Array)
  onMessage(topic, payload) {},
};
```

- `Host.Available()`: `window.lubHost.onMessage` が関数のとき `true`。
- `Host.Send(topic, payload)`: `onMessage` を同期で呼ぶ。`onMessage` が無いときや `topic` が 255 バイトを超えるときは捨てる。
- `Host.Poll(out topic, out payload)`: `queue` の先頭を取り出す。空のときは `topic` が `null`。
- ネイティブ環境では `Available()` が `false` を返し、`Send` / `Poll` は何もしない。
