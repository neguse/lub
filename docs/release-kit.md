# lub の一式 (Linux x64)

C# で書いたゲームを、web の page 1 枚にする道具一式。page は外の file を読まないので、claude.ai の artifact にそのまま載る。GitHub の release に `lub-linux-x64.tar.gz` として置き、tag (`v*`) の push で `.github/workflows/release.yml` が作る。

## 中身

| path | 中身 |
| --- | --- |
| `bin/lub` | lub 本体 (Slang 込み)。共有 library は同じディレクトリから読む |
| `bin/wasm/` | web の player (`lub export web` が page に埋め込む) |
| `bin/tcs/` | C# を Lua にする tcs (NativeAOT。.NET は要らない) |
| `cs-lib/` | API の stub (`lub_stub.cs`) と、ゲームが使う C# の library (`lubx`) |
| `manual/` | 利用者向けのガイドと、tcs で使える C# の一覧 (`tcs-support-matrix.md`) |
| `templates/game/` | ゲームの雛形 |
| `samples/boot.lua`、`third_party/lume/` | runtime が起動時に読む Lua |
| `COMMIT` | この一式を作った lub の commit |

## 用意

x86_64 の Linux で、glibc 2.39 以上 (Ubuntu 24.04 で build している)。lub が link している Vulkan の loader を入れる。export は GPU を使わない。

```sh
sudo apt-get install -y libvulkan1
curl -L -o lub-linux-x64.tar.gz https://github.com/neguse/lub/releases/latest/download/lub-linux-x64.tar.gz
tar xf lub-linux-x64.tar.gz
```

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

未対応の構文と API は、tcs が error (TCS1001 / TCS1002) にして Lua を書かない。error の行に出る名前を `manual/tcs-support-matrix.md` で引く。

## サンプルとフォント

サンプルとフォントは一式に入れていない。この一式と同じ commit (`COMMIT` に書いてある) の GitHub の `samples/` にある。フォント (M PLUS 1p の subset、OFL) は `samples/data/fonts/` にある。

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

## page にする

```sh
cd mygame
../lub/bin/lub export web Game.csproj -o out
```

`out/index.html` が page 1 枚で、これだけを置けば動く。`out/` の他の file は page に入れた中身 (snapshot)。`out` は空か、まだ無いディレクトリにする。

## 絵を確かめる

CPU の Vulkan (lavapipe) と仮想の画面を入れると、page にする前に絵を撮れる。

```sh
sudo apt-get install -y mesa-vulkan-drivers xvfb
xvfb-run -a ../lub/bin/lub Game.csproj --capture shot.png --capture-frame 60
```
