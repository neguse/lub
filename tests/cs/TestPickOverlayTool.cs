// TestPickOverlay の overlay。
using Lub;

public static class TestPickOverlayTool
{
    static int frames;
    static int waited;

    public static void OnFrame(float dt)
    {
        frames++;
        var sh = Gfx.UseShader("test_pick_overlay_tool", TestPickOverlay.Vs, TestPickOverlay.Fs, 1);
        if (sh == null)
            return;
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex, Load = Gfx.LoadAction.Load });
        TestPickOverlay.Rect(sh, -1, -1, 1, 1);
        Gfx.EndPass();
        Gfx.Size(out var w, out var h);
        if (frames == 2)
        {
            Gfx.Pick(Gfx.MainTex!, w / 2, h / 2);
            return;
        }
        if (frames < 2)
            return;
        var hit = Gfx.PickResult();
        if (hit == null)
        {
            waited++;
            Check.That(waited < 30, "no pick result in 30 frames");
            return;
        }
        Check.That(hit.Draw == 0, "draw " + hit.Draw + ", want 0 (the game's)");
        Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}
