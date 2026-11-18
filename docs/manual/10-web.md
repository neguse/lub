# web (WASM + WebGPU)

web の player は、lub の runtime と tcs VM を WASM にしたもの (`lub.js` と `lub.wasm`) で、WebGPU で描く。
ゲームの C# は native と同じものが、同じ書き方で動く。
一式では `bin/wasm/vm/` にある。

## player を載せる page

player を載せる page は次のどれか。

- lub.neguse.net のサイト: lider がゲームの C# をブラウザの中で変換して動かす。
- `lub export web` の `index.html`: snapshot と player を埋め込んだ page 1 枚 (`manual/03-lifecycle.md`)。
- 自分の page: `index.html` に script を書き足すか、`index.html` の起動の script を写して自分の page に載せる。

`index.html` の起動の script は、WebGPU の device を作って player に渡し、snapshot の file を MEMFS に置いてから `lub.js` を読む。
`Io` は MEMFS にある file だけを読むので、自分の page でも、ゲームが読む file を起動の前に置く (`manual/05-io-assets.md`)。

## ブラウザ

WebGPU が使えるブラウザで動く。

- Chrome / Edge: 137 以降。WebGPU は最初から有効。
- Safari: iPadOS / iOS / macOS 26 以降。
- Firefox Nightly: `about:config` で `dom.webgpu.enabled` を有効にする。

## page の JS とやり取りする (`Host`)

page が `window.lubHost` を定義すると、ゲームは `Host` でその page の JS と topic と payload の組をやり取りできる。
通信 (WebTransport など) や clipboard のように player が持たない機能は、page の JS に置く。

```js
window.lubHost = {
  // ゲームの Host.Poll が先頭から 1 件ずつ取り出す。payload は Uint8Array か string
  queue: [],
  // ゲームの Host.Send が届く。payload は UTF-8 の Uint8Array
  onMessage(topic, payload) {},
};
```

- `Host.Available()` は、`window.lubHost.onMessage` が関数のとき true になる。
- `Host.Send(topic, payload)` は `onMessage` を同期で呼ぶ。`onMessage` が無いときと、topic が 255 byte を超えるときは捨てる。
- `Host.Poll(out topic, out payload)` は `queue` の先頭を取り出す。空なら topic は null になる。
- native では `Available()` は false で、`Send` は何もせず、`Poll` は何も返さない。

## WebXR

一式の `bin/wasm/xr.mjs` の `createXR` を使うと、page に置いた button で immersive-vr のセッションを始めて終えられる。
ゲームの書き方は native の OpenXR と同じ (`manual/07-xr.md`)。

```js
import { createXR } from "./xr.mjs";

const xr = await createXR({
  canvas, // player が描く canvas
  button, // セッションを始めて終える button
  getModule: () => window.Module, // player の Emscripten の Module
  report: (error) => console.error(error), // セッション中の error
});
// player の初期化が済んだら
xr.ready();
```

- `createXR` は WASM を読む前に呼ぶ。
- `maxDimension` を渡すと、片眼の描画先の長い辺をその値までに抑える。
- `unlockAudio` には、利用者の操作で音を鳴らし直す処理を渡す。
- セッション中は、両眼の sRGB の出力を canvas に左右に並べ、WebGL の XR layer に送る。SDL の更新も XR の frame の中で走る。
