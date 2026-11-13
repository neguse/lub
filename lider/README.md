# lider

lub のゲームを動かしたまま、コードを書き直し、調べるためのエディタ。
lider 自身も lub の上で動く C# (TinyC#) で書いてあり、開発中のゲームを別の app として開いて動かす (`App.Use`)。

## 動かす

```
scripts/build-release.sh                 # lub
lider/scripts/build-player.sh            # lider の player (要 cargo と dotnet)
LIDER_GAME=$PWD/lider/samples/flappy/Flappy.csproj LIDER_HOME=$PWD/lider LIDER_ACTIVE=1 \
  lider/build/player/lider_player lider/src/Lider.csproj
```

- `LIDER_GAME`: 開くゲームの `.csproj`。lider が player に組み込んだ tcs で変換し (`src/Core/GameBuild.cs`)、保存した file を変換し直して、動いているゲームに当てる。
- `LIDER_HOME`: このディレクトリ (font を `data/` から読む)。
- `LIDER_ACTIVE=1`: 起動したときから編集の画面にする。無ければ F1 で出す。
- ほかの env と key は `src/Lider.cs` の先頭にある。

## ブラウザで動かす

`web/lider.html` (lub の web の page の 1 つ。`web/lider/main.ts`)。
lider は build のときに作った bytecode (`Lider.tcsb`) で動き、ゲームの C# は lider が player に組み込んだ tcs でブラウザの中で変換する。
project と lub の cs-lib は MEMFS に置き、project は保存のたびに IndexedDB に写す。
page は保存された `.cs` を整形して書き戻す。

```
source ~/emsdk/emsdk_env.sh
emcmake cmake --preset wasm-release && cmake --build build/wasm -j   # lub の wasm
lider/scripts/build-player.sh --web      # lider の player (要 rustup の wasm32-unknown-emscripten)
cd web && npm run dev                    # Lider.tcsb を作って (npm run gen-lider) 配る。http://localhost:5173/lider.html
LUB_URL=http://localhost:5173/ npm run verify-lider
```

整形は playground と同じ物を使う (`npm run gen-tcs -- --publish`)。
`?gist=<gist の id か URL>` で開くと、公開の gist の file を project にして、ゲームだけで始まる。

## 画面の組み立て

- 空間 (`Space`) が基底で、画面はそれを視点から見た絵。
- スクリーン (`Screen`、`src/Core/Screen.cs`) は空間に置く面で、上にウィンドウ (`Window`) を並べる。
- どこに並べるかは配置の規則 (`ILayout`、`src/Core/Layout.cs`) が決め、ウィンドウは中身 (`IContent`、`src/Core/Content.cs`) を 1 つ映す。
- スクリーンは、視点に固定して視野を覆う 1 枚で、ゲームの絵 (`GameView`) を映す。画面に描くのは `src/View/ScreenView.cs`。
- F10 で、保存した版 (`GameApp`) と編集中の版 (`DraftGame`。保存していない本文で変換する) のゲームを、スクリーンを左右に等分して (`Even`) 並べる。編集中の版は保存した版の状態を写して、同じ入力で動く。
- コードの窓 (`CodeWindow`) と板は、スクリーンとは別に円筒に並べ、スクリーンの手前に描く。

## 置き場所

| 場所 | 中身 |
|---|---|
| `src/` | lider の本体。entry は `src/Lider.cs`、ゲームを開いて進めるのは `src/Core/GameApp.cs` |
| `native/` | lider の player。lub の player の library (`find_package(lub)`) に、かな漢字変換 (`third_party/jamo`)、file system の操作、tcs の compiler (`tcs/` を tcs2c で C にした物) を静的に link する |
| `tests/` | runtime テスト。`tests/<name>/` は lider を動かす test、`tests/editor/` はエディタの core だけの test |
| `samples/` | lider で開くゲームの例 |
| `data/` | font |
| `scripts/` | build と test。`build-module.sh` は lider を bytecode にする |

## テスト

```
lider/scripts/gate.sh
```

かな漢字変換の辞書 (`data/mozc.dict`) はリポジトリに無い。無いと `tests/ime` は飛ばす。
