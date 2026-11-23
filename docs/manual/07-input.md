# 入力

`Input` は、そのフレームのキーボード・マウス・ゲームパッドの状態をスナップショットとして返す。

## キーボード

```csharp
if (Input.KeyPressed("space")) Jump();
if (Input.KeyDown("right")) MoveRight(dt);
if (Input.KeyReleased("left")) StopMove();
```

- `KeyDown(name)`: 押されている間ずっと `true`。
- `KeyPressed(name)`: このフレームで押された瞬間だけ `true`。
- `KeyReleased(name)`: このフレームで離された瞬間だけ `true`。

キー名は大文字・小文字を区別しない。よく使う名前は短縮名を受け付け、それ以外は SDL のスキャンコード名で解決される:

- 英数字: `"a"`〜`"z"`、`"0"`〜`"9"`
- 方向・制御: `"left"` / `"right"` / `"up"` / `"down"`、`"space"`、`"enter"`、`"escape"` (`"esc"`)、`"tab"`、`"backspace"`
- ファンクション: `"f1"`〜`"f12"`

## マウスとフォーカス

```csharp
Input.MousePos(out var mx, out var my);
Input.MouseDelta(out var dx, out var dy);
if (Input.MousePressed(1)) Shoot(mx, my);
```

- ボタン: `MouseDown` / `MousePressed` / `MouseReleased`。引数の `button` は `1` が左 (省略時の既定)、`2` が中、`3` が右。
- 座標と移動量: `MousePos(out x, out y)` はウィンドウ座標 (ピクセル)、`MouseDelta(out dx, out dy)` はそのフレーム内の相対移動量の合計。
- カーソル捕捉: `Input.CaptureMouse(true)` でカーソルを隠して相対移動を取れる (FPS 視点など)。Web では次のユーザー操作時に Pointer Lock を要求する。
- フォーカス: `Input.Focused()` はウィンドウが入力フォーカスを持っている間 `true`。

## ゲームパッド

最大 4 台 (スロット `0`〜`3`) に対応する:

```csharp
if (Input.GamepadConnected(0))
{
    var lx = Input.GamepadAxis(0, PadAxis.LeftX);
    var ly = Input.GamepadAxis(0, PadAxis.LeftY);
    if (Input.GamepadPressed(0, PadButton.South)) Jump();
}
```

- 接続状態: `GamepadConnected(player)`。
- 軸: `GamepadAxis(player, axis)`。スティックは `-1`〜`1` (右と下が正)、トリガーは `0`〜`1`。
- ボタン: `GamepadDown` / `GamepadPressed` / `GamepadReleased`。ボタン名は印字ではなく配置基準 (`PadButton.South` / `East` / `West` / `North` 等)。
- ボタンを押したまま切断されたときは `GamepadReleased` が 1 回発火する。
- Web ブラウザでは、利用者が最初にいずれかのボタンを押すまでゲームパッドが接続済みとして見えない。

## 固定 tick (`FixedStep`) との併用

`KeyPressed` や `MousePressed` のような瞬間イベントは、描画フレームが 1 回進む間に `FixedStep` の tick が 0 回しか走らないと取りこぼす。
固定 tick 内でゲームロジックを進める場合は、`Input.*` を直接読む代わりに `FixedStep` 側の `step.KeyPressed` / `step.MousePressed` / `step.GamepadPressed` を使う。
クリック座標のように位置が要る入力は、描画側で保持して次の tick で消費する。
