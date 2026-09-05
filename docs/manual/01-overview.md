# lub とは

lub は、細部までこだわったゲーム体験を作るための code-centric なゲーム制作
runtime。最重要の価値は、ゲームを止めずに変更を反映し、トライアンドエラーを
極限まで速くすること。

- ゲームコードは C#(TinyC# サブセット)で書き、Lua に transpile されて runtime 上で動く
- native(SDL3 GPU / Sokol)と web(WASM + WebGPU)の両方で同じコードが動く
- コード・アセットの変更は実行中のゲームに hot reload で即座に反映される

GUI エディタや固定のアセットパイプラインは無い。asset、描画、入力、物理、音、
診断情報をすべてコードから直接制御し、自分のゲームに必要なデータ構造と
workflow をコードで組む。

## レイヤ構成

| レイヤ | 役割 |
| --- | --- |
| C runtime | window / GPU / audio / 物理 / IO。Lua に API を公開する |
| Lua | runtime API の接点。reload 時に data shape の変化へ追従しやすい |
| C# (`Lub.*`) | Lua API への型付き宣言(`cs-lib/lub_stub.cs`)。runtime primitive そのもの |
| C# (`lubx`) | 宣言の上に C# で書かれた便利ライブラリ層(`cs-lib/lubx`) |

ゲームコードから見える API はこのリファレンスの `Lub` / `lubx` が
すべて。`Lub` は runtime が所有する最小の固い primitive、`lubx` はその上の
書き味を良くする層(詳細は「lub と lubx」の章)。

## 最小のゲーム

```csharp
using static Lub;

public static class Game
{
    public static void OnInit()
    {
        Config(new ConfigOpts());
    }

    public static void OnFrame(double dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new double[] { 0.2, 0.3, 0.4, 1.0 },
        });
        Gfx.EndPass();
    }
}
```

`OnInit` が起動時に 1 回、`OnFrame` が毎フレーム呼ばれる(詳細は
「ライフサイクルと hot reload」の章)。
