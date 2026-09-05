// lub の samples/ngs (2004 年の 2D シューティング ngs の移植) の C# 版 entry。
// 実行: lub samples/ngs/NgsMain.csproj (transpile + watch + hot reload)
// gameplay rule (敵パターン・弾数・HP・速度・出現タイミング) は原典どおりで、
// 構成は概念単位 (Scene / World / Entity / DrawList / Font)。
//
// 環境変数 (golden / debug 用):
//   LUB_NGS_BOOT = play | active | boss | gameover   起動時の scene
//   LUB_NGS_MOCK = fire | kill | <その他>              入力を script に置き換える

using static Lub;

public static class NgsMain
{
    public static void OnInit()
    {
        NgsGame.Init();
    }

    public static void OnFrame(float dt)
    {
        NgsGame.Frame(dt);
    }
}
