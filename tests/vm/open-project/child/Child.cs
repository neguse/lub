// The app that ../game/Game.cs opens with App.Use. Fills with red and outputs the counted frames in blue.
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
