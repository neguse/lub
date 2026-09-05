# ライフサイクルと hot reload

## エントリポイント

entry class(static class)は次の static メソッドを持つ(`OnFrame` 以外は任意)。

| メソッド | 呼ばれるタイミング |
| --- | --- |
| `OnInit()` | 起動時に 1 回だけ。hot reload 後は呼ばれない。`Config` はここでのみ有効 |
| `OnEvent(EventData e)` | 入力やウィンドウのイベントごと |
| `OnFrame(double dt)` | 毎フレーム。`dt` は通常、直近フレームの実測秒 |
| `OnQuit()` | 終了時に 1 回 |
| `OnReload()` | playground で編集が生きたまま反映された直後に 1 回(後述) |

Lua 側の名前は `on_init` / `on_event` / `on_frame` / `on_quit` / `on_reload`
(tcs が写す。raw Lua で書くときはこの名前)。

`dt` は固定レートではない。移動や時間経過は必ず `dt` でスケールする。
ウィンドウサイズや backend の指定は `Config`(`ConfigOpts`)で行う。

## 駆動パターン (可変 dt と固定 tick)

見た目だけの連続アニメーションは `angle += radiansPerSecond * dt` のように
実測時間を直接使う。物理やフレーム単位のゲームルールは、render とは別の
固定 60 Hz tick にする — `lubx.FixedStep` がこの分離を担う。

```csharp
static FixedStep? step; // 60 Hz、catch-up 上限 8

public static void OnFrame(double dt)
{
    step ??= new FixedStep();
    step.Frame(dt, tickDt => Update(step.KeyPressed("space")));
    DrawCurrentState(); // render は毎フレーム
}
```

`Frame()` は実測 `dt` を積み、溜まった分だけ tick を 0〜上限回実行する
(上限超過分は捨てられ、ゲームは実時間よりゆっくり進む)。`step.KeyPressed` /
`step.MousePressed` などの edge は tick 粒度で配送され、tick が 0 回だった
render frame の edge も失われない(次の tick が観測する)。tick callback は
保持されないので、hot reload の live 反映後も次のフレームから新コードが走る。

代表的な構成:

| パターン | 書き方 |
| --- | --- |
| 全部可変(見た目デモ) | FixedStep を使わず素の `OnFrame(dt)` |
| 固定 game tick + 毎フレーム render | 上のコードの形 |
| 物理だけ高頻度(例 240 Hz) | tick 内で整数 substep: `for (var i = 0; i < 4; i++) Phys3d.Step(world, tickDt / 4)` |
| game は可変、物理だけ固定 | FixedStep を物理にだけ使い、game 側は `OnFrame` で `dt` スケール |
| 低頻度の系(例 20 Hz の AI) | tick カウンタの整数分周: `if (count % 3 == 0) ai()` |

疎な 2 系なら FixedStep を 2 個持ってもよい(それぞれが独立に時間と edge を
管理する)が、フレーム内の実行順は呼んだ順に「A の全 tick → B の全 tick」に
なるため、密結合な系は 1 個の master tick からの分周で書く。

physics は `Phys2d.Begin` / `Phys3d.Begin`、body 宣言、force / torque、
`Step(tickDt)` までを同じ tick callback 内に置く。これらを render ごとに実行して
`step` だけ固定 tick にすると、物理 step が 0 回だった render の command が
次の tick へ重複して蓄積する。

マウス位置に依存する入力(タップ座標など)は per-frame の値なので、render 側で
座標ごと保持して tick で消費する。edge の bool では足りないケース
(クリック回数・押下時の座標)の実例は `18_coin_pusher` / `22_tonton` を参照。

`--fixed-dt <seconds>` を付けた起動だけは、UI と `OnFrame` に実測値ではなく
指定した同じ `dt` を毎フレーム渡す。これは capture / golden / replay のための
テスト専用オプションで、有限かつ `0 < dt <= 0.25` の値を受け付ける。render
frame の頻度そのものは変えないため、通常プレイの速度や FPS を固定する用途には
使わない。

## hot reload の仕組み

native では `lub <Entry>.csproj` で起動すると、runtime が `.cs` ソースを watch
して変更のたびに再 transpile し(tcs の watch 常駐)、生成された `.lua` の
mtime 変化を検知して module を入れ替える(`lume.hotswap`)。
web playground では in-browser の増分コンパイラが同じ流れを担う。

reload の意味論は 2 方式あり、環境で決まる。

|  | native watch | playground |
| --- | --- | --- |
| 方式 | module 全体を再評価して merge | 増分コンパイル + 差分適用 |
| static 変数の値 | 初期値に戻る | 保持される |
| クラスの形の変更 | そのまま merge | 自動で作り直し(restart) |
| 反映の速さ | 再 transpile 数百 ms〜数 s | 編集停止から 0.5 s 未満 |

どちらの方式でも共通:

- コンパイル失敗ではゲームは止まらない。古いコードのまま動き続け、
  エラーが log に出る。直せばまた反映される。
- `OnInit` は reload 後には呼ばれない。毎フレームの `OnFrame` に処理を寄せて
  「コードが常に真」になるように書くのが lub の流儀。
- GPU リソースやウィンドウ状態は runtime 側に残る(次項)。

従来方式(左列)では static 変数の初期化子が reload のたびに再実行され、
値は初期値に戻る。

GPU resource cache は reload を跨いで生きるので、`Gfx.use*` に渡す version
(内容の同一性の主張)は reload を跨いでも過去の値を再利用しないことを
保証しなければならない。素朴な static / instance のローカル counter は reload
で初期値に巻き戻ってこの保証を破り、cache に残った値と偶然一致すると更新が
黙って skip される。保証を自分で持てない(持ちたくない)なら version を
省略して「内容が変わった」を宣言する — 変更履歴は runtime が cache と同じ
寿命で管理する。詳細は「描画モデル」章の version 規約を参照。

## 何が生きたまま反映されるか(C# / playground)

playground の C# は判定が単純で、
「関数の中身」の編集は生きたまま反映、「クラスの形」の変更は自動で作り直し。

| 編集 | 挙動 |
| --- | --- |
| メソッド / プロパティ / 演算子の中身 | live 反映。static もインスタンスも、実行中の状態は全部残る。既存インスタンスも次の呼び出しから新コード |
| メソッドの追加・削除、enum メンバの変更 | live 反映(状態は残る) |
| static フィールドの追加(定数か初期化子なし) | live 反映。追加分だけ初期化される |
| static フィールドの初期値の変更 | 作り直し(実行中の値と矛盾するため) |
| インスタンスフィールドの追加・削除・初期値変更 | 作り直し(既存インスタンスを作り替えられないため) |
| 継承・クラス種別の変更、クラスの削除 | 作り直し |
| コンパイルエラー | 何も変えない(直前のコードのまま) |

どちらになったかは status 表示で分かる: `synced rev N (Xms)` = live 反映、
`restarting…` = 作り直し(ゲーム状態はリセット)。

live 反映では static が保持されるため、「コードを評価して最初に作るデータ」
(SDF メッシュなど)は自動では作り直されない。その再構築の境界が
`OnReload()`: live 反映の直後に 1 回呼ばれる。例えば `19_sdf` は `OnReload`
で `treeDirty = true` を立て、次フレームの `OnFrame` が `Model()` を再評価して
再メッシュする。

## リソースが reload を生き延びる理由

`Gfx.UseShader` / `UseBuffer` / `UseTexture` などのリソースは、変数への参照
ではなく 文字列 key + version で識別される。reload 後も同じ key で
`use*` を呼べば同じリソースがそのまま返るので、GPU 側の状態はコードの
入れ替えに影響されない。

`Use*` されなくなったリソースは `ResourceSweepAfterFrames` フレーム後に
自動破棄される。「作って解放する」ではなく「毎フレーム宣言する」モデル
(詳細は「描画モデル」の章)。
