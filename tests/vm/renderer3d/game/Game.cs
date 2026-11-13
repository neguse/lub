// Renderer3d は End() で描いた後、その frame の描画の命令を持ち続けない (次の frame まで持つと、状態の控えがそれを生かし続ける) ことの検査。
// ../child/Child.cs を App.Use で開く。
// App.Step の後に見るので、ここで見る Child.Ren は End() の後。
using System;
using Lub;

public static class Game
{
    static int frame;
    static bool done;

    static string ChildCsproj()
    {
        // GAME_CS は <dir>/game/Game.cs
        var game = Environment.GetEnvironmentVariable("GAME_CS") ?? "";
        return game.Replace("game/Game.cs", "child/Child.csproj");
    }

    static void Finish(string message, int code)
    {
        done = true;
        Console.WriteLine(message);
        App.Quit(code);
    }

    public static void OnInit()
    {
        App.Config(new ConfigOpts { Width = 64, Height = 64 });
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        var rt = Gfx.UseTexture("child", 64, 64, Gfx.PixelFormat.Rgba8, null, 1, new TextureOpts { Target = true });
        if (rt == null) return;
        var child = App.Use("child", ChildCsproj());
        var ok = App.Step(child, rt, dt);
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 0, 0, 0, 1 } });
        Gfx.EndPass();
        if (Sys.Seconds() > 120)
        {
            Finish("FAIL the opened app never drew", 1);
            return;
        }
        // build を待つ間は Step が false
        if (!ok) return;
        frame++;
        bool drawn = false;
        foreach (var n in Sys.Inspect("Child", 0, 10, child))
            if (n.Name == "Drawn" && n.Text != "0")
                drawn = true;
        if (!drawn)
            return;
        foreach (var n in Sys.Inspect("Child.Ren", 0, 100, child))
            if (n.Name == "draws")
            {
                if (n.Count != 0)
                    Finish("FAIL Renderer3d kept " + n.Count + " draws after End()", 1);
                else
                    Finish("PASS", 0);
                return;
            }
        Finish("FAIL Child.Ren has no draws", 1);
    }
}
