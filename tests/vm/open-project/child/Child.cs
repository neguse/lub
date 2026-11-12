// ../game/Game.cs が App.Use で開く app。赤で塗り、青に数えた frame を出す。
using System;
using Lub;

public static class Child
{
    static int frame;

    public static void OnFrame(float dt)
    {
        frame++;
        var blue = (frame < 255 ? frame : 255) / 255.0f;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, ClearColor = new float[] { 1.0f, 0.0f, blue, 1 } });
        Gfx.EndPass();
    }
}
