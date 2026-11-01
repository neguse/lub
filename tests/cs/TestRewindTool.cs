// TestRewind の overlay。
using Lub;

public static class TestRewindTool
{
    public static int GameCount;
    public static int GameHistory;
    public static int GameSteps;
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
        Check.That(GameSteps == GameCount, "the other class went apart: " + GameSteps + " / " + GameCount);
        if (frames == 6)
        {
            Check.That(Rewind.Count() == 6 && GameCount == 6, "recorded " + Rewind.Count() + " frames, count " + GameCount);
            // 控えは OnFrame の前の状態なので、直近は 5
            CheckHistory("TestRewind.Count", 3, "3 4 5");
            CheckHistory("RewindWorld.Steps", 2, "4 5");
            // 控えより多く頼むと、控えの分だけ
            CheckHistory("TestRewind.Count", 100, "0 1 2 3 4 5");
            Check.That(Sys.InspectHistory("TestRewind.Nothing", 2).Count == 2, "a missing field is not NaN for each frame");
            var missing = Sys.InspectHistory("TestRewind.Nothing", 2)[0];
            Check.That(missing != missing, "a missing field is not NaN");
            Check.That(Sys.InspectHistory("Nothing.Count", 2).Count == 0, "a missing path has a history");
            // 2 個前 = 4 番目の frame (始まりの数は 3)
            Rewind.Pause(2);
            Check.That(Rewind.Position() == 2, "position " + Rewind.Position());
            CheckHistory("TestRewind.Count", 2, "2 3");
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

    static void CheckHistory(string path, int count, string want)
    {
        var got = "";
        foreach (var v in Sys.InspectHistory(path, count))
            got += (got == "" ? "" : " ") + (int)v;
        Check.That(got == want, path + " history " + got + ", want " + want);
    }

    public static ErrorAction OnError(string message)
    {
        Check.Fail("overlay: " + message);
        return ErrorAction.Abort;
    }
}
