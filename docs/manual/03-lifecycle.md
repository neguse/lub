# ライフサイクルとホットリロード

## エントリポイント

エントリクラス (`static class`) には次の static メソッドを定義する (`OnFrame` 以外は任意):

| メソッド | 呼ばれるタイミング |
| --- | --- |
| `OnInit()` | 起動時に 1 回だけ。ホットリロード後は呼ばれない。`App.Config` はここでのみ有効 |
| `OnEvent(EventData e)` | 入力やウィンドウのイベントごと |
| `OnFrame(float dt)` | 毎フレーム。`dt` は直近フレームの実測秒 |
| `OnQuit()` | 終了時に 1 回 |
| `OnError(string message)` | 他のコールバックが失敗したとき。`ErrorAction` を返す |
| `OnReload()` | 編集が生きたまま反映された直後に 1 回 |
| `OnReloadNotes(string notes)` | `OnReload()` の直後、移行に関する通知事項があるとき (`tcs VM` モード) |
| `OnBuildFailed(string errors)` | ホットリロード時のビルドが失敗したとき (`tcs VM` モード) |
| `OnExport()` | `lub export` 実行時だけ `OnInit` の直後に 1 回 (`OnFrame` は呼ばれない) |

描画は `OnInit` の後に始まるため、`OnInit` 内で `Gfx.Use*` / `BeginPass` / `Draw` / `Dispatch` を呼ぶとエラーになる。リソース宣言と描画は `OnFrame` に書く。

### エラー時の処置 (`OnError`)

コールバック内でエラーが起きるとそのコールバックは打ち切られ、`OnError(message)` があれば戻り値で処置を決める:

- `ErrorAction.Log`: ログを出して次フレームへ進む (`OnError` が無いときの既定)
- `ErrorAction.Abort`: 終了コード 1 で終了する

`OnError` 自身が失敗した場合は `Abort` になる。通常の終了は `App.Quit()` (終了コード付きは `App.Quit(code)`) を使う。

## 毎フレーム宣言モデル (immediate)

`Gfx.Use*`・`Audio.Snd`・`Assets.*` などのリソースは「作って解放する」のではなく、使うあいだ毎フレーム同じ `key` で宣言し続ける。

- 無ければ作り、同じ `key` と `version` ならキャッシュを再利用する。
- 宣言が `ConfigOpts.ResourceSweepAfterFrames` (既定 120) フレーム途切れたリソースは自動破棄される。破棄済みの参照を使うとエラーになる。
- `OnFrame` がエラーで終わったフレームや、ホットリロード待ちのフレームは破棄までの数に含めない。

リソース宣言を 1 つの関数 (例: `LoadResources()`) にまとめ、`OnFrame` の冒頭と `OnExport` の両方から呼ぶのが定型:

```csharp
public static void OnFrame(float dt)
{
    if (!LoadResources())
    {
        DrawLoading();
        return;
    }
    Update(dt);
    Draw();
}

public static void OnExport()
{
    LoadResources();
}
```

## 可変 `dt` と固定 tick (`FixedStep`)

見た目だけの連続アニメーションは実測の `dt` を使う。
物理やフレーム単位のゲームルールは描画レートから切り離し、`Lubx.FixedStep` で固定 60 Hz の tick に乗せる:

```csharp
static FixedStep? step; // 既定 60 Hz、catch-up 上限 8

public static void OnFrame(float dt)
{
    step ??= new FixedStep();
    step.Frame(dt, tickDt => Update(step.KeyPressed("space")));
    DrawCurrentState();
}
```

- `step.KeyPressed` / `step.MousePressed` などは tick 粒度で配送され、tick が 0 回だったフレームの押下も次の tick まで失われない。
- 60 Hz より低い頻度 (例: 20 Hz) で動かしたい更新は、tick カウンタの剰余で間引く。
- クリック時のマウス座標などフレームごとの値が必要な入力は、描画側で座標を保持して tick で消費する。

## ホットリロード

`tcs VM` モード (`lub Game.csproj` や `lider`) では、ソースを保存するたびに差分が動いているゲームへ反映される。

| 編集内容 | 挙動 |
| --- | --- |
| メソッド・プロパティ・演算子の中身、メソッド追加・削除、enum メンバ追加 | live 反映 (static / インスタンスの状態を保持。既存インスタンスも次の呼び出しから新コード) |
| static フィールド追加 (定数・初期化子なし) | live 反映 (追加分だけ初期化) |
| static フィールド / const の初期値や型の変更 | live 反映 (その static と、それを初期化子で読む static だけ再評価。空白やコメントのみの変更では保持) |
| インスタンスフィールドの追加・削除 | live 反映 (既存インスタンスを移行。追加は初期化子、削除は破棄) |
| インスタンスフィールドの初期値変更 | live 反映 (既存インスタンスは今の値のまま。`OnReloadNotes` で通知。全体へ反映したいときは `App.Restart()`) |
| 型の削除・種別変更・継承変更、static 初期化子の削除、enum メンバ削除・値変更 | 自動リスタート (`OnQuit` → 新 VM で `OnInit`。C# の状態と物理ワールドは初期化) |
| コンパイルエラー | 何も変えず直前のコードのまま動作継続 (`OnBuildFailed` とログで通知) |

- ログには live 反映で `lub: reloaded`、リスタートで `lub: restarted` が出る。
- live 反映の直後には `OnReload()` が 1 回呼ばれる。コードから一度だけ生成したデータ (手続きメッシュ等) を再構築するきっかけに使う。
- `App.Restart()` を呼ぶとフレーム末尾で手動リスタートできる (`tcs C` モードでは何もしない)。リスタートしても `App.Config` のグラフィックスバックエンドとウィンドウサイズはプロセス再起動まで変わらない。
