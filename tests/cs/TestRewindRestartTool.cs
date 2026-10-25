// TestRewindRestart の overlay。
using Lub;

public static class TestRewindRestartTool
{
    public static int GamePhase;
    public static int GameFrames;
    static int frames;

    public static void OnInit()
    {
        Rewind.Record(10);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        if (GamePhase != 2)
            return;
        // overlay も VM と一緒に初期値へ戻るので、frames はこの VM での数
        Check.That(Rewind.Count() <= frames, "kept " + Rewind.Count() + " frames from the previous VM after " + frames + " frames");
        if (frames == 15)
        {
            Rewind.Pause(2);
            return;
        }
        if (frames == 16)
        {
            Check.That(GameFrames == 13, "paused frame drew frame " + GameFrames + ", want 13");
            Rewind.Pause(-1);
            return;
        }
        // 再開した frame (17) は 13 番目の frame をもう一度進め、次の frame が 14 番目
        if (frames == 18)
        {
            Check.That(GameFrames == 14, "the game did not continue from the paused frame: frame " + GameFrames);
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}
