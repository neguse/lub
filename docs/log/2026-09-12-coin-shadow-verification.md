> 記録: 2026-09-12 時点のコインの影の比較。再実行方法は [サンプルの README](../../samples/28_renderer_shadow/README.md) を参照。

# コインの影の原因候補の検証

対象は `18_coin_pusher`。以前の長時間観察で見えた大きな黒い縞と、
保存した配置から再現した側面の細かな格子状の縞は、まだ同じ原因と確定していない。
以下の結論は後者の 2 枚の再現例についてのもの。

## 同じ配置での比較

60 Hz の物理を 120 秒進め、投入後の描画行列を保存した。
そのうち側面に細かな縞が出る上下 2 枚を、物理を止めて拡大したものが
`coin_contact.lua`。光の向き、影の範囲、コインの形状と寸法は元のサンプルと同じ。
画像の加工で拡大せず、カメラを近づけて描画した。

Vulkan / Mesa llvmpipe で、次の比較を行った。

| 原因候補・比較 | 結果 |
| --- | --- |
| SSAO | 無効にしても縞が残る |
| 描画順に依存する Z-fighting | 2 枚の順を逆転した画像が全ピクセル一致 |
| 影の深度比較 | 影を無効にすると縞が消える |
| 1 枚の自己影だけが原因 | 下の 1 枚だけでは問題の帯がほぼ消える |
| bias が小さいだけ | 0.004 にすると縞と影の帯が消えるが、光線との交差ではその帯に影がある |
| bias を下げれば解決 | 0.0001 と 0 では上面にも自己影の縞が増える |
| 解像度が低いだけ | 2048 から 8192 にすると模様は細かくなるが帯の縞は残る |
| PCF の補間不足 | 補間だけでも格子状の模様が大幅に減る |
| 受け面の深度補正だけ | 上面の自己影を抑えられるが、側面の模様は残る |
| 受け面補正と PCF 補間 | 帯の影を保ちつつ格子状の模様が大幅に減る |

現行は nearest で読んだ深度を 9 点で比較し、等しい重みで平均する。
深度比較の結果を連続的に補間しないため、サンプル位置の変化が明暗の段差になる。
この再現例では、その模様と、bias によって本来の影が欠ける問題を確認できた。
上のコインによる影の帯自体は、形状への光線の交差でも存在する。
帯を丸ごと消すことは正しい修正の判定条件にならない。

SDL GPU でも通常と `weighted` の同様の見た目を確認した。
ただし PNG 取得時に transfer source usage の Vulkan validation error が出ており、
この実行をエラーのないバックエンド検証とは扱わない。

`weighted` は 16 点の重み付き比較を使う検証用の試作。
汎用の形状、姿勢変化、描画負荷、他の影の境界に対する影響の検証は別途必要。

## 長時間実行との照合

4,800 フレームを通常描画した画面と、そのフレームの行列だけを新規プロセスで
再描画した画面は全ピクセル一致した。これは固定した 1 条件での照合であり、
描画状態の蓄積による問題を全条件で否定するものではない。
接触しない円盤の X・Z 軸の −90〜90 度、30 度刻みの比較では、以前見えた大きな縞は再現しなかった。

## 再実行

```sh
COIN_MODE=baseline LUB_BACKEND=vulkan scripts/run-headless.sh ./build-release-linux/lub \
  samples/28_renderer_shadow/coin_contact.lua --capture /tmp/coin-baseline.png --capture-frame 3
COIN_MODE=weighted LUB_BACKEND=vulkan scripts/run-headless.sh ./build-release-linux/lub \
  samples/28_renderer_shadow/coin_contact.lua --capture /tmp/coin-weighted.png --capture-frame 3
COIN_MODE=reference LUB_BACKEND=vulkan scripts/run-headless.sh ./build-release-linux/lub \
  samples/28_renderer_shadow/coin_contact.lua --capture /tmp/coin-reference.png --capture-frame 3
```
