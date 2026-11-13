# lider

lub のゲームを動かしたまま、コードを書き直し、調べるためのエディタ。
lider 自身も lub の上で動く C# (TinyC#) で書いてあり、開発中のゲームを別の app として開いて動かす (`App.Use`)。

## 動かす

```
scripts/build-release.sh                 # lub
lider/scripts/build-player.sh            # lider の player (要 cargo)
LIDER_GAME=$PWD/lider/samples/flappy/Flappy.csproj LIDER_HOME=$PWD/lider LIDER_ACTIVE=1 \
  lider/build/player/lider_player lider/src/Lider.csproj
```

- `LIDER_GAME`: 開くゲームの `.csproj`。
- `LIDER_HOME`: このディレクトリ (font を `data/` から読む)。
- `LIDER_ACTIVE=1`: 起動したときから編集の画面にする。無ければ F1 で出す。
- ほかの env と key は `src/Lider.cs` の先頭にある。

## 置き場所

| 場所 | 中身 |
|---|---|
| `src/` | lider の本体。entry は `src/Lider.cs`、ゲームを開いて進めるのは `src/Core/GameApp.cs` |
| `native/` | lider の player。lub の player の library (`find_package(lub)`) に、かな漢字変換 (`third_party/jamo`) と file system の操作を静的に link する |
| `tests/` | runtime テスト。`tests/<name>/` は lider を動かす test、`tests/editor/` はエディタの core だけの test |
| `samples/` | lider で開くゲームの例 |
| `data/` | font |

## テスト

```
lider/scripts/gate.sh
```

かな漢字変換の辞書 (`data/mozc.dict`) はリポジトリに無い。無いと `tests/ime` は飛ばす。
