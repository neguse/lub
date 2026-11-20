# lub の一式

C# でゲームを書き、手元で動かしながら直し、web の page 1 枚にする道具一式。
GitHub の release に OS と CPU の組ごとに置く。CI (`.github/workflows/ci.yml` の kit job) が毎回作って確かめ、tag (`v*`) の push では publish job が検査を通った一式を release に載せる。

| file | 動く所 |
| --- | --- |
| `lub-linux-x64.tar.gz` | x86_64 の Linux (glibc 2.39 以上。Ubuntu 24.04 で build している) |
| `lub-linux-arm64.tar.gz` | arm64 の Linux (glibc 2.39 以上)。Steam Frame など |
| `lub-windows-x64.zip` | x64 の Windows |
| `lub-windows-arm64.zip` | arm64 の Windows |
| `lub-macos-arm64.tar.gz` | Apple silicon の macOS |

page は外の file を読まないので、claude.ai の artifact にそのまま載る。claude.ai の sandbox (x86_64 の Ubuntu 24.04) では `lub-linux-x64.tar.gz` を使う。

## 中身

| path | 中身 |
| --- | --- |
| `bin/lub` | lub 本体 (Slang 込み)。ゲームを動かし (hot reload)、検査し、export する。共有 library は同じディレクトリから読む |
| `bin/lider` (Windows は `bin/lider.cmd`) | lider を起動する。lider はゲームを動かしたまま、コードを書き直し、調べるエディタ |
| `bin/lider-player` | lider の player (lub の player に、かな漢字変換と tcs の compiler を入れたもの)。`bin/lider` が起動する |
| `bin/wasm/vm/` | web の tcs VM の player (`lub export web` が page に埋め込む) |
| `bin/wasm/xr.mjs` | 自分の page で WebXR を使うときの `createXR` (`manual/10-web.md`) |
| `bin/tcs/` | C# を tcs VM の bytecode にする tcs (tcs2c で C にした実行ファイル。.NET は要らない)。`tcs c` で C にもできる。`lub-gen tcs` は `tcs c` の C に lub の API の接続コードを作る (同じく C にした実行ファイル) |
| `lider/` | lider の bytecode (`Lider.tcsb`)、font、かな漢字変換の辞書 (`data/mozc.dict`) |
| `cs-lib/` | API の stub (`lub_stub.cs`) と、ゲームが使う C# の library (`lubx`) |
| `manual/` | 利用者向けのガイドと、tcs で使える C# の一覧 (`tcs-support-matrix.md`) |
| `templates/game/` | ゲームの雛形 |
| `LICENSE`、`THIRD_PARTY_LICENSES.md`、`third_party/` | lub と、一式に入れた第三者の物のライセンス。lider の font と辞書の表示は `lider/data/` にある |
| `COMMIT` | この一式を作った lub の commit |

## 用意

Linux では、lub が link している Vulkan の loader を入れる。

```sh
sudo apt-get install -y libvulkan1
curl -L -o lub-linux-x64.tar.gz https://github.com/neguse/lub/releases/latest/download/lub-linux-x64.tar.gz
tar xf lub-linux-x64.tar.gz
```

Windows は zip を展開する。macOS は tar.gz を展開する。
macOS で、browser で落とした一式は実行を止められるので、展開した所で `xattr -dr com.apple.quarantine lub` をする (curl で落とした物は止められない)。

`releases/latest` は正式の版だけを指す。rc (`v0.2.0-rc.1` など) は prerelease なので、`https://github.com/neguse/lub/releases/download/<tag>/<file>` で取る。

## 読むもの

ゲームを書く前に、この一式の中の次を読む。

| file | 中身 |
| --- | --- |
| `manual/08-csharp.md` | TinyC# で使えるもの・使えないものの要点 (`double` / `long` は無く `int` / `float` を使う、`try` や `async` は使えない、など)。最初に読む |
| `manual/tcs-support-matrix.md` | tcs が扱える C# の構文と BCL の一覧。使えるか迷ったらここを引く |
| `cs-lib/lub_stub.cs` | lub の API。XML doc に使い方がある |
| `cs-lib/lubx/` | lubx (Renderer3d、SpriteBatch、Text など) の C# のソース。使い分けは `manual/06-lub-lubx.md` |
| `manual/01-overview.md` | lub の全体像と最小のゲーム |
| `manual/02-coordinates.md` | 2D と 3D の座標系、論理解像度 |
| `manual/03-lifecycle.md` | `OnInit` / `OnFrame` などの入口、`lub export`、hot reload |
| `manual/04-gfx.md` | 描画 (shader、pass、texture) |
| `manual/05-io-assets.md` | file の読み込みと asset |
| `manual/07-xr.md` | XR |
| `manual/09-renderer3d-shader.md` | Renderer3d の shader を差し替えるときの頂点と uniform の約束、export のしかた |
| `manual/10-web.md` | web の player を載せる page、対応ブラウザ、page の JS とのやり取り (`Host`)、WebXR |

未対応の構文と API は、tcs が error (TCS1001 / TCS1002) にして出力を書かない。error の行に出る名前を `manual/tcs-support-matrix.md` で引く。

## サンプル

サンプルと、サンプルが使うフォントは一式に入れていない。この一式と同じ commit (`COMMIT` に書いてある) の GitHub の `samples/` にある。フォント (M PLUS 1p の subset、OFL) は `samples/data/fonts/` にある。

```sh
c=$(cat lub/COMMIT)
# 一覧
echo "https://github.com/neguse/lub/tree/$c/samples"
# 1 file ずつ取る
curl -L -O "https://raw.githubusercontent.com/neguse/lub/$c/samples/data/fonts/MPLUS1p-subset.ttf"
# 全部取る (サンプルの data を含めて数十 MB)
curl -L "https://github.com/neguse/lub/archive/$c.tar.gz" | tar xz --wildcards '*/samples/*'
```

## ゲームを書く

雛形を写して `Game.cs` を書き換える。entry の class 名は csproj の basename と同じにする (`Game.csproj` なら `public static class Game`)。何を読むかは「読むもの」にある。

```sh
cp -r lub/templates/game mygame
```

`OnExport` は export のときだけ `OnInit` の後に 1 回呼ばれる。ゲームが使う shader と file を、ふだんの API (`Gfx.UseShader`、`Io.LoadText`、`Assets.Shader` など) でここで一通り使う。使わなかった shader は page の中で compile できないので、key を名指しする error になる。

書いた直後の確認は `../lub/bin/lub check Game.csproj` でできる (`mygame` の中で)。窓を開かず、ゲームも走らせず、診断が無ければ終了コード 0、あれば 1 で終わる。

`../lub/bin/lub Game.csproj` でゲームが窓で動く。`.cs` を保存すると、動いているゲームに当たる (hot reload)。

## lider で書く

```sh
cd mygame
../lub/bin/lider Game.csproj
```

Windows では `..\lub\bin\lider.cmd Game.csproj`。

lider は雛形のゲームを動かし、lider の中で書き換えて保存した `.cs` を変換し直して、動いているゲームに当てる。起動したときはゲームだけが出る。F1 で focus をゲームとコードの間で移す。ほかの操作は lider の画面の案内に出る。かな漢字変換は一式の `lider/data/mozc.dict` を使う。

Linux の一式は XR (OpenXR) で動かせる。`LUB_BACKEND=openxr ../lub/bin/lider Game.csproj` で起動する。OpenXR の runtime (Steam Frame なら SteamVR) を先に動かしておく。

## page にする

```sh
cd mygame
../lub/bin/lub export web Game.csproj -o out
```

`out/index.html` が page 1 枚で、これだけを置けば動く。`out/` の他の file は page に入れた中身 (snapshot)。`out` は空か、まだ無いディレクトリにする。

## 絵を確かめる

Linux では、CPU の Vulkan (lavapipe) と仮想の画面を入れると、GPU も画面も無い所で絵を撮れる。

```sh
sudo apt-get install -y mesa-vulkan-drivers xvfb
xvfb-run -a ../lub/bin/lub Game.csproj --capture shot.png --capture-frame 60
```
