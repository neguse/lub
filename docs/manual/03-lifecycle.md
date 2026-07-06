# ライフサイクルと hot reload

## エントリポイント

ゲームクラスは 3 つの static 関数を持つ。

| 関数 | 呼ばれるタイミング |
| --- | --- |
| `main()` | module ロード時に 1 回。hot reload でも再実行されるので、通常は空にする |
| `onInit()` | 起動時に 1 回だけ。**hot reload 後は呼ばれない**。`Lub.config` はここでのみ有効 |
| `onFrame(dt:Float)` | 毎フレーム。`dt` は直近フレームの実測秒 |

`dt` は固定レートではない。移動や時間経過は必ず `dt` でスケールする。
ウィンドウサイズや backend の指定は `Lub.config`(または env 補完付きの
`lubx.Boot.config`)で行う。

## hot reload の仕組み

native では `lub <entry.hxml>` で起動すると、runtime が `.hx` ソースを watch
して変更のたびに再 transpile し(`haxe --wait` 常駐で 100〜300ms)、生成された
`.lua` の mtime 変化を検知して module を入れ替える(`lume.hotswap`)。
web playground では in-browser Haxe コンパイラが同じ流れを担う。

知っておくべき挙動:

- **コンパイル失敗ではゲームは止まらない**。古いコードのまま動き続け、
  エラーが log に出る。直せばまた反映される。
- **static 変数の初期化子は reload のたびに再実行され、値は初期値に戻る**。
  reload しても遊びが途切れないのは、GPU リソースやウィンドウ状態が runtime
  側に残るため(次項)。
- `onInit` は reload 後には呼ばれない。毎フレームの `onFrame` に処理を寄せて
  「コードが常に真」になるように書くのが lub の流儀。

## リソースが reload を生き延びる理由

`Gfx.useShader` / `useBuffer` / `useTexture` などのリソースは、変数への参照
ではなく **文字列 key + version** で識別される。reload 後も同じ key で
`use*` を呼べば同じリソースがそのまま返るので、GPU 側の状態はコードの
入れ替えに影響されない。

`use*` されなくなったリソースは `resource_sweep_after_frames` フレーム後に
自動破棄される。「作って解放する」ではなく「毎フレーム宣言する」モデル
(詳細は「描画モデル」の章)。
