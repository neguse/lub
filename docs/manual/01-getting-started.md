# はじめる

lub は、コード中心でゲームを作るためのゲームエンジン。
ゲームコードは C# のサブセット (TinyC#) で書き、ゲームを動かしたままコードを書き換えて即座に反映 (ホットリロード) しながら制作を進める。

## 試す・始める

### ブラウザで試す

[lub.neguse.net](https://lub.neguse.net) を開くと、専用エディタ (lider) 上でそのままコードを書いて動かせる。サンプルもメニューから開ける。

### ローカルで動かす

GitHub Releases から自分の環境向けアーカイブを展開する。

- Windows: 追加の依存は不要 (アーカイブを展開するだけ)。
- Linux: Vulkan loader (`libvulkan1` 等) が必要。
- macOS: ブラウザからダウンロードした場合は展開後に `xattr -dr com.apple.quarantine lub` を実行する。

同梱の雛形 (`templates/game`) をコピーして始める。

Windows (PowerShell):

```powershell
Copy-Item -Recurse lub\templates\game mygame
cd mygame
..\lub\bin\lub.exe Game.csproj         # ウィンドウで起動 (保存でホットリロード)
..\lub\bin\lub.exe check Game.csproj   # 型・サブセット検査のみ (ウィンドウなし)
..\lub\bin\lider.cmd Game.csproj       # 専用エディタ (lider) で開く
```

Linux / macOS:

```sh
cp -r lub/templates/game mygame
cd mygame
../lub/bin/lub Game.csproj         # ウィンドウで起動 (保存でホットリロード)
../lub/bin/lub check Game.csproj   # 型・サブセット検査のみ (ウィンドウなし)
../lub/bin/lider Game.csproj       # 専用エディタ (lider) で開く
```

`Game.csproj` のファイル名 (拡張子なし) がエントリクラス名になる (`Game.csproj` なら `public static class Game`)。
コピーしたプロジェクトを `dotnet run` (`.NET` モード) でも動かす場合は、`Game.csproj` の `LubRoot` を展開した `lub` ディレクトリに合わせて書き換える (`lub` CLI での実行には不要)。

## 最小のゲーム

```csharp
using Lub;

public static class Game
{
    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 640, Height = 360 });
    }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts
        {
            Target = Gfx.MainTex,
            ClearColor = new float[] { 0.1f, 0.15f, 0.25f, 1.0f },
        });
        Gfx.EndPass();
    }
}
```

- `OnInit()` は起動時に 1 回呼ばれる。ウィンドウ設定などはここで行う。
- `OnFrame(float dt)` は毎フレーム呼ばれる。描画やリソース宣言はここに書く。
- ゲームから使う API は、最小限の土台である `Lub API` (`using Lub;`) と、C# で書かれた便利層の `Lubx API` (`using Lubx;`) の 2 層で構成される。
