// Ui.Render が runtime の UI の shader を使って描く。export の backend では、その shader も export 先の target で compile される (native-gate.sh が見る)。
using System;
using Lub;

public static class TestUiRender
{
    static int frames;

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Backend = Environment.GetEnvironmentVariable("LUB_BACKEND") ?? "sdlgpu" });
    }

    // lub export は 1 frame 分を描いて、使う shader と file を記録する
    public static void OnExport() { OnFrame(0); }

    public static void OnFrame(float dt)
    {
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        if (Ui.BeginWindow("ui"))
            Ui.Text("hello");
        Ui.EndWindow();
        Ui.Render();
        Gfx.EndPass();
        if (++frames >= 3)
            Check.Pass();
    }

    public static ErrorAction OnError(string message) { return Check.OnError(message); }
}
