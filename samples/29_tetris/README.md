# Blocks

lub の既存機能で作った小さなテトリス風ゲーム。10 列 × 20 行、7 種のミノを
一巡ごとに並べ替える。次のミノと着地位置を表示する。

## 起動

リポジトリのルートで依存を揃え、Release ビルドを作ってから起動する。

```powershell
git submodule update --init
pwsh -NoProfile -ExecutionPolicy Bypass -File .\scripts\build-release.ps1
.\build-release\lub.exe samples/29_tetris/Tetris29.csproj
```

Linux は `bash scripts/build-release.sh` でビルドし、
`./build-release-linux/lub samples/29_tetris/Tetris29.csproj` で起動する。
このサンプルは native CLI 用で、Web playground の一覧には含めていない。

## 操作

| キー | 操作 |
| --- | --- |
| 左右 | 移動。押し続けると連続移動 |
| 上 / X | 右回転 |
| Z | 左回転 |
| 下 | 速く落下 |
| Space | 着地位置へ落として固定 |
| P | 一時停止・再開 |
| R | 最初からやり直す |

10 ラインごとに自然落下が速くなる。1 / 2 / 3 / 4 ライン消去は
100 / 300 / 500 / 800 点 × 消去前のレベル。速い落下は 1 マス 1 点、
即落下は 1 マス 2 点。レベル表示は 1 から始まる。
横方向の簡易回転補正を使う。SRS、ホールド、固定猶予、効果音、記録の保存はない。
乱数は再現確認のため固定 seed で、再開すると同じ順番になる。

## 検証

ゲームルールだけの検査は GPU 不要。

```powershell
dotnet run --project tests/tetris/TetrisTests.csproj
dotnet build samples/29_tetris/Tetris29.csproj
```

Windows でルール検査と Lua 実行のログ検査をまとめて行う場合は
`pwsh -File tests/tetris/verify.ps1` を使う。Lua エラー、期待したスコアの欠落、
撮影通知の欠落を失敗として扱う。ルール検査は native CI にも含まれる。

4 ライン消去の場面を再現する場合は次を実行する。
出力先ディレクトリは先に作る。

```powershell
New-Item -ItemType Directory -Force out/tetris-trial | Out-Null
$env:TETRIS_SCENARIO = 'clear'
.\build-release\lub.exe samples/29_tetris/Tetris29.csproj --capture out/tetris-trial/clear.png --capture-frame 35 --fixed-dt 0.016666667
Remove-Item Env:TETRIS_SCENARIO
```

frame 1 で下 4 段に縦穴を作り、frame 10 で I ミノを回転、frame 30 で即落下する。
期待ログは `TETRIS_SCENARIO lines=4 score=832 locked=1`。
frame 5 を撮ると消去前、frame 35 を撮ると消去後になる。
これはゲームのメソッドを直接呼ぶ検査で、OS のキー入力経路の検査ではない。

制作中の失敗・判断と振り返りは
[作業ログ](../../docs/log/2026-09-09-tetris-ai-trial.md) にある。
フォントは `data/MPLUS1p-OFL.txt` のライセンスに従う。
