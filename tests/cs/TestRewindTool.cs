// TestRewind の overlay。
using Lub;

public static class TestRewindTool
{
    public static int GameCount;
    public static int GameHistory;
    static int frames;
    static int pausedAt;
    static int seen;

    public static void OnInit()
    {
        Rewind.Record(10);
    }

    public static void OnFrame(float dt)
    {
        frames++;
        Check.That(GameHistory == GameCount, "the List and the count went apart: " + GameHistory + " / " + GameCount);
        if (frames == 6)
        {
            Check.That(Rewind.Count() == 6 && GameCount == 6, "recorded " + Rewind.Count() + " frames, count " + GameCount);
            // 2 個前 = 4 番目の frame (始まりの数は 3)
            Rewind.Pause(2);
            Check.That(Rewind.Position() == 2, "position " + Rewind.Position());
            return;
        }
        if (frames >= 7 && frames <= 9)
        {
            Check.That(GameCount == 4, "paused frame drew count " + GameCount + ", want 4");
            if (frames == 9)
                Rewind.Pause(-1);
            return;
        }
        if (frames == 10)
        {
            Check.That(Rewind.Position() == -1, "still paused");
            // 再開した frame は 4 番目の frame をもう一度進めた所から
            Check.That(GameCount == 4 && Rewind.Count() == 4, "resumed at count " + GameCount + " with " + Rewind.Count() + " frames");
            return;
        }
        if (frames == 11)
        {
            Check.That(GameCount == 5, "the game did not continue: count " + GameCount);
            Check.Pass();
        }
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}
