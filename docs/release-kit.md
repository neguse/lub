# lub の一式 (Linux x64)

C# で書いたゲームを、web の page 1 枚にする道具一式。page は外の file を読まないので、claude.ai の artifact にそのまま載る。GitHub の release に `lub-linux-x64.tar.gz` として置き、tag (`v*`) の push で `.github/workflows/release.yml` が作る。

## 中身

| path | 中身 |
| --- | --- |
| `bin/lub` | lub 本体 (Slang 込み)。共有 library は同じディレクトリから読む |
| `bin/wasm/` | web の player (`lub export web` が page に埋め込む) |
| `bin/tcs/` | C# を Lua にする tcs (NativeAOT。.NET は要らない) |
| `cs-lib/` | API の stub (`lub_stub.cs`) と、ゲームが使う C# の library (`lubx`) |
| `manual/` | 利用者向けのガイド |
| `templates/game/` | ゲームの雛形 |
| `samples/boot.lua`、`third_party/lume/` | runtime が起動時に読む Lua |

## 用意

x86_64 の Linux で、glibc 2.39 以上 (Ubuntu 24.04 で build している)。lub が link している Vulkan の loader を入れる。export は GPU を使わない。

```sh
sudo apt-get install -y libvulkan1
curl -L -o lub-linux-x64.tar.gz https://github.com/neguse/lub/releases/latest/download/lub-linux-x64.tar.gz
tar xf lub-linux-x64.tar.gz
```

## ゲームを書く

雛形を写して `Game.cs` を書き換える。entry の class 名は csproj の basename と同じにする (`Game.csproj` なら `public static class Game`)。API は `cs-lib/lub_stub.cs` の XML doc と `manual/` にある。

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
