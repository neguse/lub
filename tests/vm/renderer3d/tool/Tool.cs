// Renderer3d は End() で描いた後、その frame の描画の命令を持ち続けない (次の frame まで持つと、Rewind の控えがそれを生かし続ける) ことの検査。
// overlay の OnFrame はゲームの OnFrame の後に呼ばれるので、ここで見る Game.Ren は End() の後。
using System;
using Lub;

public static class Tool
{
    static int frame;
    static bool done;

    static void Finish(string message, int code)
    {
        done = true;
        Console.WriteLine(message);
        App.Quit(code);
    }

    public static void OnFrame(float dt)
    {
        if (done)
            return;
        frame++;
        bool drawn = false;
        foreach (var n in Sys.Inspect("Game", 0, 10))
            if (n.Name == "Drawn" && n.Text != "0")
                drawn = true;
        if (!drawn)
        {
            if (frame > 60)
                Finish("FAIL the game never drew", 1);
            return;
        }
        foreach (var n in Sys.Inspect("Game.Ren", 0, 100))
            if (n.Name == "draws")
            {
                if (n.Count != 0)
                    Finish("FAIL Renderer3d kept " + n.Count + " draws after End()", 1);
                else
                    Finish("PASS", 0);
                return;
            }
        Finish("FAIL Game.Ren has no draws", 1);
    }
}
