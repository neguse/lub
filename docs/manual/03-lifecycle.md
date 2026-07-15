# ライフサイクルと hot reload

## エントリポイント

ゲームクラスは次の static 関数を持つ(`onReload` のみ任意)。

| 関数 | 呼ばれるタイミング |
| --- | --- |
| `main()` | module ロード時に 1 回。hot reload でも再実行されるので、通常は空にする |
| `onInit()` | 起動時に 1 回だけ。**hot reload 後は呼ばれない**。`Lub.config` はここでのみ有効 |
| `onFrame(dt:Float)` | 毎フレーム。`dt` は通常、直近フレームの実測秒 |
| `onReload()`(任意) | C#(playground)で編集が生きたまま反映された直後に 1 回(後述) |

`dt` は固定レートではない。移動や時間経過は必ず `dt` でスケールする。
ウィンドウサイズや backend の指定は `Lub.config`(または env 補完付きの
`lubx.Boot.config`)で行う。

## 駆動パターン (可変 dt と固定 tick)

見た目だけの連続アニメーションは `angle += radiansPerSecond * dt` のように
実測時間を直接使う。物理やフレーム単位のゲームルールは、render とは別の
固定 60 Hz tick にする — `lubx.FixedStep` がこの分離を担う。

```haxe
static var step = new FixedStep(); // 60 Hz、catch-up 上限 8

public static function onFrame(dt:Float) {
	step.frame(dt, tickDt -> update(step.keyPressed(Key.Space)));
	drawCurrentState(); // render は毎フレーム
}
```

`frame()` は実測 `dt` を積み、溜まった分だけ tick を 0〜上限回実行する
(上限超過分は捨てられ、ゲームは実時間よりゆっくり進む)。`step.keyPressed` /
`step.mousePressed` などの edge は tick 粒度で配送され、**tick が 0 回だった
render frame の edge も失われない**(次の tick が観測する)。tick callback は
保持されないので、hot reload の live 反映後も次のフレームから新コードが走る。

代表的な構成:

| パターン | 書き方 |
| --- | --- |
| 全部可変(見た目デモ) | FixedStep を使わず素の `onFrame(dt)` |
| 固定 game tick + 毎フレーム render | 上のコードの形 |
| 物理だけ高頻度(例 240 Hz) | tick 内で整数 substep: `for (i in 0...4) Phys3d.step(world, tickDt / 4)` |
| game は可変、物理だけ固定 | FixedStep を物理にだけ使い、game 側は `onFrame` で `dt` スケール |
| 低頻度の系(例 20 Hz の AI) | tick カウンタの整数分周: `if (count % 3 == 0) ai()` |

疎な 2 系なら FixedStep を 2 個持ってもよい(それぞれが独立に時間と edge を
管理する)が、フレーム内の実行順は呼んだ順に「A の全 tick → B の全 tick」に
なるため、密結合な系は 1 個の master tick からの分周で書く。

physics は `Phys2d.begin` / `Phys3d.begin`、body 宣言、force / torque、
`step(tickDt)` までを同じ tick callback 内に置く。これらを render ごとに実行して
`step` だけ固定 tick にすると、物理 step が 0 回だった render の command が
次の tick へ重複して蓄積する。

マウス位置に依存する入力(タップ座標など)は per-frame の値なので、render 側で
座標ごと保持して tick で消費する。edge の bool では足りないケース
(クリック回数・押下時の座標)の実例は `18_coin_pusher` / `22_tonton` を参照。

`--fixed-dt <seconds>` を付けた起動だけは、UI と `onFrame` に実測値ではなく
指定した同じ `dt` を毎フレーム渡す。これは capture / golden / replay のための
テスト専用オプションで、有限かつ `0 < dt <= 0.25` の値を受け付ける。render
frame の頻度そのものは変えないため、通常プレイの速度や FPS を固定する用途には
使わない。

## hot reload の仕組み

native では `lub <entry.hxml>` で起動すると、runtime が `.hx` ソースを watch
して変更のたびに再 transpile し(`haxe --wait` 常駐で 100〜300ms)、生成された
`.lua` の mtime 変化を検知して module を入れ替える(`lume.hotswap`)。
web playground では in-browser コンパイラが同じ流れを担う。

reload の意味論は 2 方式あり、環境で決まる。

|  | Haxe(native / playground)、C#(native watch) | C#(playground) |
| --- | --- | --- |
| 方式 | module 全体を再評価して merge | 増分コンパイル + 差分適用 |
| static 変数の値 | **初期値に戻る** | **保持される** |
| クラスの形の変更 | そのまま merge | 自動で作り直し(restart) |
| 反映の速さ | 再 transpile 数百 ms〜数 s | 編集停止から 0.5 s 未満 |

どちらの方式でも共通:

- **コンパイル失敗ではゲームは止まらない**。古いコードのまま動き続け、
  エラーが log に出る。直せばまた反映される。
- `onInit` は reload 後には呼ばれない。毎フレームの `onFrame` に処理を寄せて
  「コードが常に真」になるように書くのが lub の流儀。
- GPU リソースやウィンドウ状態は runtime 側に残る(次項)。

従来方式(左列)では **static 変数の初期化子が reload のたびに再実行され、
値は初期値に戻る**。

GPU resource cache は reload を跨いで生きるので、`Gfx.use*` に渡す version
(内容の同一性の主張)は **reload を跨いでも過去の値を再利用しない**ことを
保証しなければならない。素朴な static / instance のローカル counter は reload
で初期値に巻き戻ってこの保証を破り、cache に残った値と偶然一致すると更新が
黙って skip される。保証を自分で持てない(持ちたくない)なら version を
省略して「内容が変わった」を宣言する — 変更履歴は runtime が cache と同じ
寿命で管理する。詳細は「描画モデル」章の version 規約を参照。

## 何が生きたまま反映されるか(C# / playground)

playground の C# は判定が単純で、
**「関数の中身」の編集は生きたまま反映、「クラスの形」の変更は自動で作り直し**。

| 編集 | 挙動 |
| --- | --- |
| メソッド / プロパティ / 演算子の**中身** | **live 反映**。static もインスタンスも、実行中の状態は全部残る。既存インスタンスも次の呼び出しから新コード |
| メソッドの追加・削除、enum メンバの変更 | live 反映(状態は残る) |
| static フィールドの追加(定数か初期化子なし) | live 反映。追加分だけ初期化される |
| static フィールドの**初期値の変更** | 作り直し(実行中の値と矛盾するため) |
| インスタンスフィールドの追加・削除・初期値変更 | 作り直し(既存インスタンスを作り替えられないため) |
| 継承・クラス種別の変更、クラスの削除 | 作り直し |
| コンパイルエラー | 何も変えない(直前のコードのまま) |

どちらになったかは status 表示で分かる: `synced rev N (Xms)` = live 反映、
`restarting…` = 作り直し(ゲーム状態はリセット)。

live 反映では static が保持されるため、「コードを評価して最初に作るデータ」
(SDF メッシュなど)は自動では作り直されない。その再構築の境界が
`onReload()`: live 反映の直後に 1 回呼ばれる。例えば `19_sdf` は `onReload`
で `treeDirty = true` を立て、次フレームの `onFrame` が `Model()` を再評価して
再メッシュする。

## リソースが reload を生き延びる理由

`Gfx.useShader` / `useBuffer` / `useTexture` などのリソースは、変数への参照
ではなく **文字列 key + version** で識別される。reload 後も同じ key で
`use*` を呼べば同じリソースがそのまま返るので、GPU 側の状態はコードの
入れ替えに影響されない。

`use*` されなくなったリソースは `resource_sweep_after_frames` フレーム後に
自動破棄される。「作って解放する」ではなく「毎フレーム宣言する」モデル
(詳細は「描画モデル」の章)。
