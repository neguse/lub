// TestOverlay の overlay。
// overlay の callback が失敗したら overlay の OnError に届くので、そこで失敗にする。
using Lub;

public static class TestOverlayTool
{
    public static int Inits;
    public static int Frames;

    public static void OnInit()
    {
        Check.That(TestOverlay.Inits == 1, "the overlay OnInit ran before the game OnInit");
        Inits++;
    }

    public static void OnFrame(float dt)
    {
        Frames++;
        Check.That(TestOverlay.Frames == Frames, "the overlay OnFrame did not follow the game OnFrame");
        Gfx.BeginPass(new PassOpts { Target = Gfx.MainTex });
        Gfx.EndPass();
        Input.Grab(false, false);
        if (Frames == 3)
            Check.Pass();
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}
