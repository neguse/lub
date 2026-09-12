# Renderer3d の影

床と浮いた箱だけを、固定カメラと固定の平行光源で描く。
`18_coin_pusher` と同じ `Renderer3d` を使う。
SSAO、bloom、FXAA は無効。影の補正値は既定の `0.004` のままにする。

```sh
./build-release-linux/lub samples/28_renderer_shadow/RendererShadow28.csproj
```

Web playground では `28_renderer_shadow` を選ぶ。
G キーで水色の枠、S キーで影を切り替える。

床上面は `y=0`。箱の範囲は `x,z=[-0.5,0.5]`、`y=[0.5,1.5]`。
光へ向かう方向は `(1,2,0)` なので、頂点 `(x,y,z)` の影は
`(x-y/2,0,z)` になる。箱全体の影は `x=[-1.25,0.25]`、
`z=[-0.5,0.5]` の長方形で、水色の枠はこの範囲を示す。

枠はちらつきを避けるため床から `0.006` 浮かせ、影を落とさず、受けない設定で描く。
影の補正と輪郭のぼかしにより、描画された境界は枠と厳密には一致しない。
まず位置・方向・形が合うかを確認し、G キーで枠を消して境界を観察する。

## コイン 2 枚の比較

`coin_contact.lua` は、上下に重なるコイン 2 枚の接地影を描く native 用サンプル。
物理、bloom、FXAA、dither は使わない。
リポジトリの root から起動する。

```sh
COIN_MODE=fixed ./build-release-linux/lub samples/28_renderer_shadow/coin_contact.lua
COIN_MODE=reference ./build-release-linux/lub samples/28_renderer_shadow/coin_contact.lua
```

S キーは影、A キーは SSAO、O キーは上のコインの表示を切り替える。
`reference` では 2 枚の形状を使って光線との交差を計算するため、O キーは使えない。
比較方式は起動時の `COIN_MODE` で選ぶ。

| 値 | 比較条件 |
| --- | --- |
| `baseline` | 中央の受け面の深度で周囲 9 点を比較。省略時の値、bias は 0.001 |
| `fixed` | Renderer3d 本体。bias はコインと同じ 0.0001 |
| `shadow_off` / `ao_off` / `clean` | 影なし / SSAO なし / 両方なし |
| `single` / `reverse` | 下の 1 枚だけ / 描画順を反転 |
| `bias_high` / `bias_low` / `bias_zero` | bias を 0.004 / 0.0001 / 0 に変更 |
| `high_res` / `one_tap` | 影の解像度を 8192 に変更 / 中央の 1 点だけ比較 |
| `receiver_plane` | 読み取り位置に合わせて受け面の深度を補正。bias は 0.000005 |
| `weighted_only` | PCF の補間のみ。bias は 0.001 |
| `weighted` | 受け面の深度補正と PCF の補間。bias は 0.000005 |
| `reference` | 同じ 24 角柱 2 枚への光線の交差で求める、ぼかしのない比較用の影 |

`fixed` 以外は比較用の `coin_shadow_legacy.slang` を使う。
Renderer3d 本体の接地影を見るには `fixed`、形状と光線の交差による影を見るには `reference` を選ぶ。
`reference` の逆行列は固定した 2 枚に対応するので、配置を変える場合は再計算が必要。

## 傾いたコイン 1 枚の自己影

`coin_acne.lua` は、光にほぼ平行なコイン 1 枚を描く native 用サンプル。
この平らな面は光に向いており、ほかの遮蔽物もないので、影 ON/OFF で縞が出てはいけない。
通常起動は Renderer3d 本体を使う。`legacy` は比較用のシェーダーで自己影の縞を再現する。

```sh
./build-release-linux/lub samples/28_renderer_shadow/coin_acne.lua
COIN_MODE=legacy ./build-release-linux/lub samples/28_renderer_shadow/coin_acne.lua
```

S キーで影、space キーで微小な移動・回転を切り替える。
SSAO、bloom、FXAA、dither は無効。
